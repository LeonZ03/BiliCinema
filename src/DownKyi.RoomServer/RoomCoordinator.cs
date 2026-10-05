using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace DownKyi.RoomServer;

internal sealed class ClientConnection(WebSocket socket)
{
    private readonly Channel<string> outgoing = Channel.CreateBounded<string>(new BoundedChannelOptions(128)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait
    });

    public WebSocket Socket { get; } = socket;
    public Room? Room { get; set; }

    public void Enqueue(string message)
    {
        if (!outgoing.Writer.TryWrite(message))
        {
            Socket.Abort();
        }
    }

    public void Complete() => outgoing.Writer.TryComplete();

    public async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        await foreach (string message in outgoing.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(message);
            await Socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }

        if (Socket.State == WebSocketState.Open)
        {
            await Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "room ended", cancellationToken)
                .ConfigureAwait(false);
        }
    }
}

internal sealed class MemberSlot(string clientId)
{
    public string ClientId { get; } = clientId;
    public ClientConnection? Connection { get; set; }
    public bool Ready { get; set; }
    public bool Buffering { get; set; }
    public DateTimeOffset? DisconnectedAt { get; set; }

    public object View => new { online = Connection is not null, ready = Ready, buffering = Buffering };
}

internal sealed class Room(string code, string hostClientId)
{
    public object Gate { get; } = new();
    public string Code { get; } = code;
    public MemberSlot Host { get; } = new(hostClientId);
    public MemberSlot? Guest { get; set; }
    public MediaIdentity? Media { get; set; }
    public double PositionSeconds { get; set; }
    public double Rate { get; set; } = 1;
    public bool Playing { get; set; }
    public bool StartRequested { get; set; }
    public bool Closed { get; set; }
    public long Version { get; set; } = 1;
    public long AnchorTimestamp { get; set; } = Stopwatch.GetTimestamp();

    public double CurrentPosition()
    {
        double elapsed = Playing ? Stopwatch.GetElapsedTime(AnchorTimestamp).TotalSeconds * Rate : 0;
        return Math.Min(86400, PositionSeconds + elapsed);
    }

    public void Settle()
    {
        PositionSeconds = CurrentPosition();
        AnchorTimestamp = Stopwatch.GetTimestamp();
    }

    public bool CanRun() => Media is not null && Host.Connection is not null && Host.Ready && !Host.Buffering &&
        (Guest is not { Connection: not null } || Guest is { Ready: true, Buffering: false });

    public void StartIfReady()
    {
        if (StartRequested && CanRun())
        {
            AnchorTimestamp = Stopwatch.GetTimestamp();
            Playing = true;
            StartRequested = false;
        }
    }

    public void PauseForDisconnect()
    {
        Settle();
        Playing = false;
        StartRequested = false;
    }

    public void ContinueWithoutGuest()
    {
        var resume = Playing || StartRequested;
        Settle();
        Playing = false;
        StartRequested = resume;
        StartIfReady();
    }

    public object Snapshot() => new
    {
        version = Version,
        media = Media,
        positionSeconds = CurrentPosition(),
        playing = Playing,
        rate = Rate,
        serverTimeUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        waitingForReady = StartRequested && !Playing,
        memberCount = (Host.Connection is null ? 0 : 1) + (Guest?.Connection is null ? 0 : 1),
        host = Host.View,
        guest = Guest?.View
    };

    public void Broadcast()
    {
        string message = WireProtocol.Encode(new { type = "snapshot", snapshot = Snapshot() });
        Host.Connection?.Enqueue(message);
        Guest?.Connection?.Enqueue(message);
    }
}

internal sealed class RoomCoordinator : BackgroundService
{
    private const int MaxRooms = 256;
    private static readonly TimeSpan HostReconnectWindow = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan GuestReconnectWindow = TimeSpan.FromMinutes(5);
    private readonly object creationGate = new();
    private readonly ConcurrentDictionary<string, Room> rooms = new(StringComparer.Ordinal);

    public void Attach(ClientConnection connection, JsonElement element)
    {
        WireMessage message = WireProtocol.Parse(element);
        if (message.Type == "create")
        {
            Room room;
            lock (creationGate)
            {
                if (rooms.Count >= MaxRooms)
                {
                    throw new RoomProtocolException("server_full");
                }

                do
                {
                    room = new Room(NewSecret(), NewSecret());
                }
                while (!rooms.TryAdd(room.Code, room));
            }

            lock (room.Gate)
            {
                room.Host.Connection = connection;
                connection.Room = room;
                Welcome(connection, room, room.Host, "host");
            }

            return;
        }

        if (message.Type != "join" || message.RoomCode is null)
        {
            throw new RoomProtocolException("join_or_create_required");
        }

        if (!rooms.TryGetValue(message.RoomCode, out Room? existing))
        {
            throw new RoomProtocolException("room_unavailable");
        }

        lock (existing.Gate)
        {
            if (existing.Closed)
            {
                throw new RoomProtocolException("room_unavailable");
            }

            MemberSlot slot;
            string role;
            bool preserveWaitingStart = message.ClientId is null && existing.Guest is null &&
                (existing.StartRequested || existing.Playing);
            if (message.ClientId is not null)
            {
                if (message.ClientId == existing.Host.ClientId)
                {
                    slot = existing.Host;
                    role = "host";
                }
                else if (existing.Guest?.ClientId == message.ClientId)
                {
                    slot = existing.Guest;
                    role = "guest";
                }
                else
                {
                    throw new RoomProtocolException("room_unavailable");
                }
            }
            else
            {
                if (existing.Guest is not null)
                {
                    throw new RoomProtocolException("room_full");
                }

                slot = existing.Guest = new MemberSlot(NewSecret());
                role = "guest";
            }

            slot.Connection?.Socket.Abort();
            slot.Connection = connection;
            slot.DisconnectedAt = null;
            slot.Ready = false;
            slot.Buffering = false;
            connection.Room = existing;
            existing.PauseForDisconnect();
            existing.StartRequested = preserveWaitingStart;
            existing.Version++;
            Welcome(connection, existing, slot, role);
            existing.Broadcast();
        }
    }

    public void Handle(ClientConnection connection, JsonElement element)
    {
        WireMessage message = WireProtocol.Parse(element);
        Room room = connection.Room ?? throw new RoomProtocolException("not_joined");
        lock (room.Gate)
        {
            if (room.Closed)
            {
                throw new RoomProtocolException("room_closed");
            }

            bool isHost = ReferenceEquals(room.Host.Connection, connection);
            MemberSlot? member = isHost ? room.Host : room.Guest;
            if (!ReferenceEquals(member?.Connection, connection))
            {
                throw new RoomProtocolException("not_joined");
            }

            if (message.Type == "ping")
            {
                connection.Enqueue(WireProtocol.Encode(new
                {
                    type = "pong",
                    nonce = message.Nonce,
                    clientTimeUnixMs = message.ClientTimeUnixMs,
                    serverTimeUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                }));
                return;
            }

            if (message.Type == "leave")
            {
                Leave(room, member!, isHost);
                return;
            }

            if (message.Type == "close")
            {
                RequireHost(isHost);
                Close(room);
                return;
            }

            switch (message.Type)
            {
                case "select":
                    RequireHost(isHost);
                    room.Media = message.Media;
                    room.PositionSeconds = 0;
                    room.AnchorTimestamp = Stopwatch.GetTimestamp();
                    room.Playing = false;
                    room.StartRequested = false;
                    room.Host.Ready = false;
                    room.Host.Buffering = false;
                    if (room.Guest is not null)
                    {
                        room.Guest.Ready = false;
                        room.Guest.Buffering = false;
                    }

                    break;
                case "play":
                    RequireHost(isHost);
                    RequireMedia(room);
                    room.Settle();
                    room.StartRequested = true;
                    room.StartIfReady();
                    break;
                case "pause":
                    RequireHost(isHost);
                    room.Settle();
                    room.Playing = false;
                    room.StartRequested = false;
                    break;
                case "seek":
                    RequireHost(isHost);
                    RequireMedia(room);
                    room.PositionSeconds = message.PositionSeconds!.Value;
                    room.AnchorTimestamp = Stopwatch.GetTimestamp();
                    break;
                case "rate":
                    RequireHost(isHost);
                    RequireMedia(room);
                    room.Settle();
                    room.Rate = message.Rate!.Value;
                    break;
                case "ready":
                    RequireMedia(room);
                    member!.Ready = message.Ready!.Value;
                    if (!member.Ready && room.Playing)
                    {
                        room.Settle();
                        room.Playing = false;
                        room.StartRequested = true;
                    }

                    room.StartIfReady();
                    break;
                case "buffering":
                    RequireMedia(room);
                    member!.Buffering = message.Buffering!.Value;
                    if (member.Buffering && room.Playing)
                    {
                        room.Settle();
                        room.Playing = false;
                        room.StartRequested = true;
                    }

                    room.StartIfReady();
                    break;
                default:
                    throw new RoomProtocolException("invalid_command");
            }

            room.Version++;
            room.Broadcast();
        }
    }

    public static void Detach(ClientConnection connection)
    {
        Room? room = connection.Room;
        if (room is null)
        {
            return;
        }

        lock (room.Gate)
        {
            MemberSlot? slot = ReferenceEquals(room.Host.Connection, connection) ? room.Host :
                ReferenceEquals(room.Guest?.Connection, connection) ? room.Guest : null;
            if (slot is null || room.Closed)
            {
                return;
            }

            slot.Connection = null;
            slot.DisconnectedAt = DateTimeOffset.UtcNow;
            slot.Ready = false;
            slot.Buffering = false;
            if (ReferenceEquals(slot, room.Host))
            {
                room.PauseForDisconnect();
            }
            else
            {
                room.ContinueWithoutGuest();
            }
            room.Version++;
            room.Broadcast();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach (Room room in rooms.Values)
            {
                lock (room.Gate)
                {
                    if (room.Closed)
                    {
                        continue;
                    }

                    if (room.Host.DisconnectedAt is { } hostAt && now - hostAt >= HostReconnectWindow)
                    {
                        Close(room);
                    }
                    else if (room.Guest?.DisconnectedAt is { } guestAt && now - guestAt >= GuestReconnectWindow)
                    {
                        room.Guest = null;
                        room.Version++;
                        room.Broadcast();
                    }
                }
            }
        }
    }

    private static void Welcome(ClientConnection connection, Room room, MemberSlot slot, string role) =>
        connection.Enqueue(WireProtocol.Encode(new
        {
            type = "welcome",
            roomCode = room.Code,
            clientId = slot.ClientId,
            role,
            snapshot = room.Snapshot()
        }));

    private void Leave(Room room, MemberSlot slot, bool isHost)
    {
        if (isHost)
        {
            Close(room);
            return;
        }

        slot.Connection?.Enqueue(WireProtocol.Encode(new { type = "left" }));
        slot.Connection?.Complete();
        slot.Connection!.Room = null;
        room.Guest = null;
        room.ContinueWithoutGuest();
        room.Version++;
        room.Broadcast();
    }

    private void Close(Room room)
    {
        if (room.Closed)
        {
            return;
        }

        room.Closed = true;
        room.Host.Connection?.Enqueue(WireProtocol.Encode(new { type = "closed" }));
        room.Guest?.Connection?.Enqueue(WireProtocol.Encode(new { type = "closed" }));
        room.Host.Connection?.Complete();
        room.Guest?.Connection?.Complete();
        rooms.TryRemove(room.Code, out _);
    }

    private static void RequireHost(bool isHost)
    {
        if (!isHost)
        {
            throw new RoomProtocolException("host_only");
        }
    }

    private static void RequireMedia(Room room)
    {
        if (room.Media is null)
        {
            throw new RoomProtocolException("select_media_first");
        }
    }

    private static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
