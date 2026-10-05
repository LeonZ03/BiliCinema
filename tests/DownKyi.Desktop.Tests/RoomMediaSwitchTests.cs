using System.Net.WebSockets;
using System.Text.Json;
using DownKyi.RoomServer;

namespace DownKyi.Desktop.Tests;

public sealed class RoomMediaSwitchTests
{
    [Fact]
    public void HostCanSelectAnotherVideoWithoutRecreatingRoom()
    {
        using var rooms = new RoomCoordinator();
        using var hostSocket = new TestWebSocket();
        var host = new ClientConnection(hostSocket);
        Attach(rooms, host, """{"type":"create"}""");
        var room = Assert.IsType<Room>(host.Room);

        Handle(rooms, host, """{"type":"select","media":{"episodeId":101}}""");
        Handle(rooms, host, """{"type":"ready","ready":true}""");
        Handle(rooms, host, """{"type":"play"}""");
        Assert.True(room.Playing);

        Handle(rooms, host, """{"type":"select","media":{"bvid":"BV1234567890","aid":42,"cid":78}}""");
        Assert.Equal("BV1234567890", room.Media?.Bvid);
        Assert.Equal(78, room.Media?.Cid);
        Assert.False(room.Playing);
        Assert.False(room.Host.Ready);

        using var guestSocket = new TestWebSocket();
        var guest = new ClientConnection(guestSocket);
        Attach(rooms, guest, $$"""{"type":"join","roomCode":"{{room.Code}}"}""");
        Assert.Same(room, guest.Room);
        Assert.Equal("BV1234567890", guest.Room?.Media?.Bvid);
    }

    [Fact]
    public void DisconnectedGuestDoesNotBlockNewGuest()
    {
        using var rooms = new RoomCoordinator();
        using var hostSocket = new TestWebSocket();
        var host = new ClientConnection(hostSocket);
        Attach(rooms, host, """{"type":"create"}""");
        var room = Assert.IsType<Room>(host.Room);
        Handle(rooms, host, """{"type":"select","media":{"episodeId":101}}""");
        Handle(rooms, host, """{"type":"ready","ready":true}""");
        Handle(rooms, host, """{"type":"play"}""");
        Assert.True(room.Playing);
        using var firstGuestSocket = new TestWebSocket();
        var firstGuest = new ClientConnection(firstGuestSocket);
        Attach(rooms, firstGuest, $$"""{"type":"join","roomCode":"{{room.Code}}"}""");
        Assert.True(room.StartRequested);
        Handle(rooms, firstGuest, """{"type":"ready","ready":true}""");
        Assert.True(room.Playing);
        var firstId = room.Guest?.ClientId;

        RoomCoordinator.Detach(firstGuest);
        Assert.True(room.Playing);
        using var replacementSocket = new TestWebSocket();
        var replacement = new ClientConnection(replacementSocket);
        Attach(rooms, replacement, $$"""{"type":"join","roomCode":"{{room.Code}}"}""");

        Assert.Same(room, replacement.Room);
        Assert.NotEqual(firstId, room.Guest?.ClientId);
        Assert.Same(replacement, room.Guest?.Connection);
        Assert.True(room.StartRequested);
        Handle(rooms, replacement, """{"type":"ready","ready":true}""");
        Assert.True(room.Playing);
    }

    private static void Attach(RoomCoordinator rooms, ClientConnection connection, string json)
    {
        using var document = JsonDocument.Parse(json);
        rooms.Attach(connection, document.RootElement);
    }

    private static void Handle(RoomCoordinator rooms, ClientConnection connection, string json)
    {
        using var document = JsonDocument.Parse(json);
        rooms.Handle(connection, document.RootElement);
    }

    private sealed class TestWebSocket : WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State { get; } = WebSocketState.Open;
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
            bool endOfMessage, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
