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
    public string MemberId { get; } = Guid.NewGuid().ToString("N");
    public ClientConnection? Connection { get; set; }
    public bool Ready { get; set; }
    public bool Buffering { get; set; }
    public DateTimeOffset? DisconnectedAt { get; set; }

    public object View => new { memberId = MemberId, online = Connection is not null, ready = Ready, buffering = Buffering };
}

internal sealed class Room(string code, string hostClientId)
{
    public const int MaxMembers = 5;
    public object Gate { get; } = new();
    public string Code { get; } = code;
    public MemberSlot Host { get; } = new(hostClientId);
    public List<MemberSlot> Guests { get; } = [];
    public IEnumerable<MemberSlot> Members => Guests.Prepend(Host);
    public MediaIdentity? Media { get; set; }
    public double PositionSeconds { get; set; }
    public double Rate { get; set; } = 1;
    public bool Playing { get; set; }
    public bool StartRequested { get; set; }
    public bool Closed { get; set; }
    public long Version { get; set; } = 1;
    public long SyncRevision { get; set; }
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
        Guests.All(guest => guest.Connection is null || guest is { Ready: true, Buffering: false });

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

    public void ContinueWithConnectedMembers()
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
        syncRevision = SyncRevision,
        media = Media,
        positionSeconds = CurrentPosition(),
        playing = Playing,
        rate = Rate,
        serverTimeUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        waitingForReady = StartRequested && !Playing,
        memberCount = Members.Count(member => member.Connection is not null),
        capacity = MaxMembers,
        host = Host.View,
        guests = Guests.Select(guest => guest.View).ToArray()
    };

    public void Broadcast()
    {
        string message = WireProtocol.Encode(new { type = "snapshot", snapshot = Snapshot() });
        SendToMembers(message);
    }

    public void SendToMembers(string message, bool guestsOnly = false)
    {
        foreach (var member in guestsOnly ? Guests : Members)
        {
            member.Connection?.Enqueue(message);
        }
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
            bool preserveWaitingStart = false;
            if (message.ClientId is not null)
            {
                if (message.ClientId == existing.Host.ClientId)
                {
                    slot = existing.Host;
                    role = "host";
                }
                else if (existing.Guests.Find(guest => guest.ClientId == message.ClientId) is { } guest)
                {
                    slot = guest;
                    role = "guest";
                }
                else
                {
                    throw new RoomProtocolException("room_unavailable");
                }
            }
            else
            {
                if (existing.Guests.Count >= Room.MaxMembers - 1)
                {
                    // Keep reconnect credentials while there is room, but an offline guest
                    // must not prevent a new viewer from using a vacant seat.
                    var offline = existing.Guests.Where(guest => guest.Connection is null)
                        .MinBy(guest => guest.DisconnectedAt);
                    if (offline is null)
                    {
                        throw new RoomProtocolException("room_full");
                    }
                    existing.Guests.Remove(offline);
                }

                slot = new MemberSlot(NewSecret());
                existing.Guests.Add(slot);
                role = "guest";
            }

            if (role == "guest")
            {
                preserveWaitingStart = existing.StartRequested || existing.Playing;
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
            MemberSlot? member = room.Members.FirstOrDefault(slot => ReferenceEquals(slot.Connection, connection));
            if (!ReferenceEquals(member?.Connection, connection))
            {
                throw new RoomProtocolException("not_joined");
            }

            if (HandleSpecialMessage(room, connection, member!, isHost, message))
            {
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
                    foreach (var slot in room.Members)
                    {
                        slot.Ready = false;
                        slot.Buffering = false;
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
                case "sync":
                    RequireHost(isHost);
                    RequireMedia(room);
                    SynchronizePlayback(room, message);
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

            if (isHost && message.Type is ("select" or "play" or "pause" or "seek" or "rate"))
            {
                room.SendToMembers(WireProtocol.Encode(new
                {
                    type = "hostAction",
                    action = message.Type,
                    positionSeconds = message.PositionSeconds,
                    rate = message.Rate
                }), guestsOnly: true);
            }
            room.Version++;
            room.Broadcast();
        }
    }

    private static void SynchronizePlayback(Room room, WireMessage message)
    {
        if (room.Media != message.Media)
        {
            throw new RoomProtocolException("media_changed");
        }
        // Commit one authoritative player sample before announcing readiness.
        // A web player may restore history after the room selected this media.
        room.PositionSeconds = message.PositionSeconds!.Value;
        room.Rate = message.Rate!.Value;
        room.AnchorTimestamp = Stopwatch.GetTimestamp();
        room.Playing = false;
        room.StartRequested = message.Playing!.Value;
        room.StartIfReady();
        room.SyncRevision++;
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
            MemberSlot? slot = room.Members.FirstOrDefault(member => ReferenceEquals(member.Connection, connection));
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
                room.ContinueWithConnectedMembers();
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
                    else if (room.Guests.RemoveAll(guest => guest.DisconnectedAt is { } guestAt
                             && now - guestAt >= GuestReconnectWindow) > 0)
                    {
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
            memberId = slot.MemberId,
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
        room.Guests.Remove(slot);
        room.ContinueWithConnectedMembers();
        room.Version++;
        room.Broadcast();
    }

    private bool HandleSpecialMessage(Room room, ClientConnection connection, MemberSlot member, bool isHost,
        WireMessage message)
    {
        switch (message.Type)
        {
            case "ping":
                connection.Enqueue(WireProtocol.Encode(new
                {
                    type = "pong",
                    nonce = message.Nonce,
                    clientTimeUnixMs = message.ClientTimeUnixMs,
                    serverTimeUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                }));
                return true;
            case "leave":
                Leave(room, member, isHost);
                return true;
            case "close":
                RequireHost(isHost);
                Close(room);
                return true;
            case "chat":
                string chat = WireProtocol.Encode(new
                {
                    type = "chat",
                    memberId = member.MemberId,
                    role = isHost ? "host" : "guest",
                    nickname = message.ChatNickname,
                    text = message.ChatText,
                    sentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });
                room.SendToMembers(chat);
                return true;
            default:
                return false;
        }
    }

    private void Close(Room room)
    {
        if (room.Closed)
        {
            return;
        }

        room.Closed = true;
        room.SendToMembers(WireProtocol.Encode(new { type = "closed" }));
        foreach (var member in room.Members)
        {
            member.Connection?.Complete();
        }
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
