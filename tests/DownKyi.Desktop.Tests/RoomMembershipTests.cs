using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DownKyi.RoomServer;
using DownKyi.Services.Watch;

namespace DownKyi.Desktop.Tests;

public sealed class RoomMembershipTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    [Fact]
    public async Task FivePeopleCanJoinEmptyRoomAndSixthIsRejected()
    {
        using var fixture = new RoomFixture();
        var guests = fixture.FillRoom();
        var rejected = fixture.NewConnection();
        var error = Assert.Throws<RoomProtocolException>(() => fixture.Join(rejected));
        Assert.Equal("room_full", error.Message);
        Assert.Null(rejected.Room);
        Assert.Null(fixture.Room.Media);
        Assert.Equal(4, fixture.Room.Guests.Count);

        await fixture.DrainAsync();
        foreach (var connection in guests.Prepend(fixture.Host))
        {
            using var message = JsonDocument.Parse(fixture.Messages(connection)[^1]);
            var snapshot = message.RootElement.GetProperty("snapshot");
            Assert.Equal(5, snapshot.GetProperty("memberCount").GetInt32());
            Assert.Equal(5, snapshot.GetProperty("capacity").GetInt32());
            Assert.Equal(4, snapshot.GetProperty("guests").GetArrayLength());
        }
    }

    [Fact]
    public void PlaybackWaitsForEveryOnlineMemberAndResumesAfterBufferingGuestLeaves()
    {
        using var fixture = new RoomFixture();
        var guests = fixture.FillRoom();
        fixture.Send(fixture.Host, """{"type":"select","media":{"episodeId":101}}""");
        fixture.Ready(fixture.Host);
        fixture.Send(fixture.Host, """{"type":"play"}""");
        foreach (var guest in guests.Take(3)) fixture.Ready(guest);
        Assert.False(fixture.Room.Playing);
        Assert.True(fixture.Room.StartRequested);
        fixture.Ready(guests[3]);
        Assert.True(fixture.Room.Playing);

        fixture.Send(guests[1], """{"type":"buffering","buffering":true}""");
        fixture.Send(guests[2], """{"type":"buffering","buffering":true}""");
        Assert.False(fixture.Room.Playing);
        RoomCoordinator.Detach(guests[1]);
        Assert.False(fixture.Room.Playing);
        fixture.Send(guests[2], """{"type":"leave"}""");
        Assert.True(fixture.Room.Playing);
        Assert.Null(guests[2].Room);
        Assert.Equal(2, fixture.Room.Guests.Count(slot => slot.Connection != null));
    }

    [Fact]
    public void GuestReconnectReclaimsOnlyItsOwnSeatAndStaleConnectionCannotControlIt()
    {
        using var fixture = new RoomFixture();
        var guests = fixture.FillRoom();
        var slot = fixture.Room.Guests[2];
        var memberId = slot.MemberId;
        RoomCoordinator.Detach(guests[2]);
        var reconnected = fixture.NewConnection();
        fixture.Join(reconnected, slot.ClientId);
        Assert.Same(reconnected, slot.Connection);
        Assert.Equal(memberId, slot.MemberId);
        Assert.Equal(4, fixture.Room.Guests.Count);
        Assert.Same(guests[0], fixture.Room.Guests[0].Connection);
        Assert.Throws<RoomProtocolException>(() => fixture.Send(guests[2], """{"type":"leave"}"""));
        RoomCoordinator.Detach(guests[2]);
        Assert.Same(reconnected, slot.Connection);

        RoomCoordinator.Detach(reconnected);
        var newcomer = fixture.NewConnection();
        fixture.Join(newcomer);
        Assert.Equal(4, fixture.Room.Guests.Count);
        Assert.DoesNotContain(slot, fixture.Room.Guests);
        Assert.Throws<RoomProtocolException>(() => fixture.Join(fixture.NewConnection(), slot.ClientId));
    }

    [Fact]
    public async Task MediaActionsAndChatReachAllMembersWithoutSharingReconnectCredentials()
    {
        using var fixture = new RoomFixture();
        var guests = fixture.FillRoom();
        fixture.Send(fixture.Host, """{"type":"select","media":{"episodeId":101}}""");
        foreach (var connection in guests.Prepend(fixture.Host)) fixture.Ready(connection);
        fixture.Send(fixture.Host, """{"type":"play"}""");
        fixture.Send(fixture.Host, """{"type":"seek","positionSeconds":120}""");
        fixture.Send(fixture.Host, """{"type":"pause"}""");
        fixture.Send(fixture.Host, """{"type":"rate","rate":1.5}""");
        fixture.Send(fixture.Host, """{"type":"select","media":{"episodeId":102}}""");
        fixture.Send(fixture.Host, """{"type":"sync","media":{"episodeId":102},"positionSeconds":42,"rate":1,"playing":false}""");
        Assert.All(fixture.Room.Members, member => Assert.False(member.Ready));
        fixture.Send(guests[2], """{"type":"chat","nickname":"观众三","text":"大家好"}""");

        await fixture.DrainAsync();
        foreach (var connection in guests.Prepend(fixture.Host))
        {
            var events = fixture.Messages(connection).Select(value => JsonSerializer.Deserialize<JsonElement>(value)).ToArray();
            var chat = Assert.Single(events, value => value.GetProperty("type").GetString() == "chat");
            Assert.Equal(fixture.Room.Guests[2].MemberId, chat.GetProperty("memberId").GetString());
            Assert.Equal("大家好", chat.GetProperty("text").GetString());
            var snapshot = events.Last(value => value.GetProperty("type").GetString() == "snapshot")
                .GetProperty("snapshot");
            Assert.Equal(102, snapshot.GetProperty("media").GetProperty("episodeId").GetInt64());
            Assert.Equal(42, snapshot.GetProperty("positionSeconds").GetDouble());
            Assert.Equal(1, snapshot.GetProperty("syncRevision").GetInt64());
            foreach (var shared in events.Where(value => value.GetProperty("type").GetString() != "welcome"))
            {
                foreach (var member in fixture.Room.Members)
                    Assert.DoesNotContain(member.ClientId, shared.GetRawText(), StringComparison.Ordinal);
            }
            if (connection == fixture.Host) continue;
            var actions = events.Where(value => value.GetProperty("type").GetString() == "hostAction")
                .Select(value => value.GetProperty("action").GetString()!).ToArray();
            Assert.Equal(["select", "play", "seek", "pause", "rate", "select"], actions);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EveryGuestUsesItsOwnReadyStateAndCannotIssueHostCommands(int index)
    {
        using var fixture = new RoomFixture();
        var guests = fixture.FillRoom();
        fixture.Send(fixture.Host, """{"type":"select","media":{"episodeId":101}}""");
        foreach (var connection in guests.Where((_, i) => i != index).Prepend(fixture.Host)) fixture.Ready(connection);
        var snapshot = JsonSerializer.Deserialize<WatchRoomSnapshot>(WireProtocol.Encode(fixture.Room.Snapshot()), JsonOptions)!;
        Assert.False(snapshot.AllOnlineGuestsReady);
        Assert.False(snapshot.FindMember(fixture.Room.Guests[index].MemberId)!.Ready);
        Assert.True(snapshot.FindMember(fixture.Room.Host.MemberId)!.Ready);
        foreach (var command in new[]
        {
            """{"type":"select","media":{"episodeId":102}}""",
            """{"type":"play"}""", """{"type":"pause"}""",
            """{"type":"seek","positionSeconds":123}""", """{"type":"rate","rate":2}""",
            """{"type":"sync","media":{"episodeId":101},"positionSeconds":10,"rate":1,"playing":true}""",
            """{"type":"close"}"""
        })
        {
            Assert.Equal("host_only", Assert.Throws<RoomProtocolException>(() => fixture.Send(guests[index], command)).Message);
        }
    }

    [Fact]
    public async Task HostReconnectKeepsOtherMembersAndClosingNotifiesEveryone()
    {
        using var fixture = new RoomFixture();
        var guests = fixture.FillRoom();
        fixture.Send(fixture.Host, """{"type":"select","media":{"episodeId":101}}""");
        foreach (var connection in guests.Prepend(fixture.Host)) fixture.Ready(connection);
        fixture.Send(fixture.Host, """{"type":"play"}""");
        RoomCoordinator.Detach(fixture.Host);
        Assert.False(fixture.Room.Playing);
        var reconnectedHost = fixture.NewConnection();
        fixture.Join(reconnectedHost, fixture.Room.Host.ClientId);
        fixture.Ready(reconnectedHost);
        fixture.Send(reconnectedHost, """{"type":"play"}""");
        Assert.True(fixture.Room.Playing);
        fixture.Send(reconnectedHost, """{"type":"close"}""");
        Assert.True(fixture.Room.Closed);
        Assert.Throws<RoomProtocolException>(() => fixture.Join(fixture.NewConnection()));
        await fixture.DrainAsync();
        foreach (var connection in guests.Append(reconnectedHost))
        {
            using var message = JsonDocument.Parse(fixture.Messages(connection)[^1]);
            Assert.Equal("closed", message.RootElement.GetProperty("type").GetString());
        }
    }

    private sealed class RoomFixture : IDisposable
    {
        private readonly RoomCoordinator rooms = new();
        private readonly Dictionary<ClientConnection, RecordingSocket> sockets = [];
        public ClientConnection Host { get; }
        public Room Room { get; }

        public RoomFixture()
        {
            Host = NewConnection();
            using var create = JsonDocument.Parse("""{"type":"create"}""");
            rooms.Attach(Host, create.RootElement);
            Room = Host.Room!;
        }

        public ClientConnection NewConnection()
        {
            var socket = new RecordingSocket();
            var connection = new ClientConnection(socket);
            sockets.Add(connection, socket);
            return connection;
        }

        public ClientConnection[] FillRoom() => Enumerable.Range(0, 4).Select(_ =>
        {
            var connection = NewConnection();
            Join(connection);
            return connection;
        }).ToArray();

        public void Join(ClientConnection connection, string? credential = null)
        {
            using var document = JsonDocument.Parse(credential == null
                ? $$"""{"type":"join","roomCode":"{{Room.Code}}"}"""
                : $$"""{"type":"join","roomCode":"{{Room.Code}}","clientId":"{{credential}}"}""");
            rooms.Attach(connection, document.RootElement);
        }

        public void Send(ClientConnection connection, string message)
        {
            using var document = JsonDocument.Parse(message);
            rooms.Handle(connection, document.RootElement);
        }

        public void Ready(ClientConnection connection) => Send(connection, """{"type":"ready","ready":true}""");
        public List<string> Messages(ClientConnection connection) => sockets[connection].Messages;

        public async Task DrainAsync()
        {
            foreach (var connection in sockets.Keys)
            {
                connection.Complete();
                await connection.SendLoopAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            rooms.Dispose();
            foreach (var socket in sockets.Values) socket.Dispose();
        }
    }

    private sealed class RecordingSocket : WebSocket
    {
        public List<string> Messages { get; } = [];
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription,
            CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription,
            CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType,
            bool endOfMessage, CancellationToken cancellationToken)
        {
            Messages.Add(Encoding.UTF8.GetString(buffer));
            return Task.CompletedTask;
        }
    }
}
