using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DownKyi.Services.Watch;

internal sealed class WatchRoomClient : IAsyncDisposable
{
    private const int MaxMessageBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _receiveCancellation;
    private Task? _receiveTask;
    private bool _receiveEnded = true;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private long _lastPingUnixMs;

    public event Action<WatchRoomSnapshot>? SnapshotReceived;
    public event Action<WatchRoomChatMessage>? ChatReceived;
    public event Action<WatchRoomHostAction>? HostActionReceived;
    public event Action? Disconnected;
    public event Action? Closed;

    public string? RoomCode { get; private set; }
    public string? ClientId { get; private set; }
    public string? MemberId { get; private set; }
    public bool IsHost { get; private set; }
    public double ClockOffsetMilliseconds { get; private set; }
    public double RoundTripMilliseconds { get; private set; }
    public bool HasClockEstimate { get; private set; }
    public bool Connected => !_receiveEnded && _socket?.State == WebSocketState.Open;
    public CancellationToken ConnectionCancellation => _receiveCancellation?.Token ?? new CancellationToken(true);

    public async Task<WatchRoomSnapshot> ConnectAsync(
        string serviceAddress,
        bool create,
        string? roomCode,
        CancellationToken cancellationToken,
        string? routeKey = null)
    {
        if (!Uri.TryCreate(serviceAddress, UriKind.Absolute, out var uri)
            || !((uri.Scheme == "wss") || (uri.Scheme == "ws" && uri.IsLoopback))
            || uri.AbsolutePath != "/ws")
        {
            throw new InvalidOperationException("房间地址须为 wss://…/ws；仅本机可使用 ws://127.0.0.1:端口/ws。");
        }

        if (!create && !string.IsNullOrWhiteSpace(roomCode))
            uri = new UriBuilder(uri) { Query = $"room={Uri.EscapeDataString(roomCode)}" }.Uri;

        var reconnectClientId = !create && string.Equals(roomCode, RoomCode, StringComparison.Ordinal)
            ? ClientId
            : null;
        await CloseSocketAsync().ConfigureAwait(false);
        _receiveEnded = true;
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        socket.Options.CollectHttpResponseDetails = true;
        if (!string.IsNullOrWhiteSpace(routeKey))
        {
            socket.Options.SetRequestHeader("X-BiliCinema-Route-Key", routeKey);
        }
        _socket = socket;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await socket.ConnectAsync(uri, deadline.Token).ConfigureAwait(false);
            _receiveCancellation = new CancellationTokenSource();
            var requestedRoom = create ? null : roomCode ?? RoomCode;
            if (!create && string.IsNullOrWhiteSpace(requestedRoom))
            {
                throw new InvalidOperationException("请输入房间邀请码。");
            }

            if (create)
            {
                await SendAsync(new { type = "create" }, deadline.Token).ConfigureAwait(false);
            }
            else if (reconnectClientId != null)
            {
                await SendAsync(new { type = "join", roomCode = requestedRoom, clientId = reconnectClientId },
                    deadline.Token).ConfigureAwait(false);
            }
            else
            {
                await SendAsync(new { type = "join", roomCode = requestedRoom }, deadline.Token)
                    .ConfigureAwait(false);
            }
            using var welcome = await ReceiveOneAsync(socket, deadline.Token).ConfigureAwait(false);
            var type = welcome.RootElement.GetProperty("type").GetString();
            if (type != "welcome")
            {
                var code = welcome.RootElement.TryGetProperty("code", out var errorCode)
                    ? errorCode.GetString() : null;
                throw new InvalidOperationException(code switch
                {
                    "room_full" => "房间已满（最多 5 人，含房主），请等待成员离开后重试。",
                    "room_unavailable" => "房间已结束或邀请码无效，请向房主获取新邀请。",
                    "server_full" => "房间服务暂时已满，请稍后重试。",
                    _ => "房间拒绝加入，请检查邀请码和连接。"
                });
            }

            var root = welcome.RootElement;
            if (!root.TryGetProperty("memberId", out var memberId)
                || string.IsNullOrEmpty(memberId.GetString()))
            {
                throw new InvalidOperationException("房间版本不兼容，请房主和所有访客一起更新 BiliCinema。");
            }
            RoomCode = root.GetProperty("roomCode").GetString();
            ClientId = root.GetProperty("clientId").GetString();
            MemberId = memberId.GetString();
            IsHost = root.GetProperty("role").GetString() == "host";
            var snapshot = root.GetProperty("snapshot").Deserialize<WatchRoomSnapshot>(JsonOptions)
                           ?? throw new InvalidDataException("房间状态为空。");
            _receiveEnded = false;
            _receiveTask = ReceiveLoopAsync(_receiveCancellation.Token);
            return snapshot;
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            await CloseSocketAsync().ConfigureAwait(false);
            throw new InvalidOperationException("连接房间超时，请检查网络或让房主重新创建房间并分享新邀请。", error);
        }
        catch (WebSocketException error)
        {
            var status = socket.HttpStatusCode;
            await CloseSocketAsync().ConfigureAwait(false);
            throw new InvalidOperationException(status switch
            {
                HttpStatusCode.NotFound
                    => "房间已结束或邀请已失效，请向房主获取新邀请。",
                HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout
                    => $"房间公网通道暂不可用（HTTP {(int)status}），请房主重新创建房间并分享新邀请。",
                HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
                    => $"房间连接被拒绝（HTTP {(int)status}），请检查双方的网络或代理设置。",
                _ => "无法连接房间通道，请检查网络和邀请地址；若持续失败，请房主重新创建房间。"
            }, error);
        }
        catch
        {
            await CloseSocketAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task SendAsync(object message, CancellationToken cancellationToken)
    {
        var socket = _socket;
        if (socket?.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("房间连接已断开。");
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (payload.Length > MaxMessageBytes)
        {
            throw new InvalidOperationException("房间消息超过大小限制。");
        }

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public Task PingAsync(CancellationToken cancellationToken)
    {
        _lastPingUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return SendAsync(new { type = "ping", clientTimeUnixMs = _lastPingUnixMs }, cancellationToken);
    }

    public async Task CloseRoomAsync(CancellationToken cancellationToken)
    {
        if (!IsHost || !Connected || _receiveTask == null)
            throw new InvalidOperationException("当前没有可结束的房主房间。");
        var receiving = _receiveTask;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await SendAsync(new { type = "close" }, deadline.Token).ConfigureAwait(false);
            // Wait for the server's closed frame before shutting down the route.
            await receiving.WaitAsync(deadline.Token).ConfigureAwait(false);
            if (RoomCode != null) throw new InvalidOperationException("房间关闭确认未收到，连接已断开。");
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("房间关闭确认超时，将释放本机房间服务。", error);
        }
    }

    public async Task LeaveAsync()
    {
        await CloseSocketAsync().ConfigureAwait(false);
        RoomCode = null;
        ClientId = null;
        MemberId = null;
        IsHost = false;
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var socket = _socket ?? throw new InvalidOperationException("房间连接尚未建立。");
        var roomClosed = false;
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                using var message = await ReceiveOneAsync(socket, cancellationToken).ConfigureAwait(false);
                var root = message.RootElement;
                var type = root.GetProperty("type").GetString();
                if (type == "snapshot")
                {
                    var snapshot = root.GetProperty("snapshot")
                        .Deserialize<WatchRoomSnapshot>(JsonOptions);
                    if (snapshot != null)
                    {
                        SnapshotReceived?.Invoke(snapshot);
                    }
                }
                else if (type == "chat")
                {
                    var chat = ParseChat(root);
                    if (chat != null)
                    {
                        ChatReceived?.Invoke(chat);
                    }
                }
                else if (type == "hostAction")
                {
                    HandleHostAction(root);
                }
                else if (type == "pong"
                         && root.TryGetProperty("clientTimeUnixMs", out var sent)
                         && root.TryGetProperty("serverTimeUnixMs", out var server))
                {
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var sentMs = sent.GetInt64();
                    if (sentMs == _lastPingUnixMs && now >= sentMs)
                    {
                        var roundTrip = now - sentMs;
                        var offset = server.GetInt64() - (sentMs + now) / 2.0;
                        // Reject unusually slow samples. A tunnel can occasionally queue a ping.
                        if (!HasClockEstimate || roundTrip < Math.Max(250, RoundTripMilliseconds * 2))
                        {
                            ClockOffsetMilliseconds = HasClockEstimate
                                ? ClockOffsetMilliseconds * 0.75 + offset * 0.25 : offset;
                            RoundTripMilliseconds = HasClockEstimate
                                ? RoundTripMilliseconds * 0.75 + roundTrip * 0.25 : roundTrip;
                            HasClockEstimate = true;
                        }
                    }
                }
                else if (type == "closed")
                {
                    roomClosed = true;
                    _receiveEnded = true;
                    RoomCode = null;
                    ClientId = null;
                    MemberId = null;
                    IsHost = false;
                    Closed?.Invoke();
                    break;
                }
            }
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException
            or IOException or InvalidDataException or JsonException or ObjectDisposedException)
        {
            // The view model reports a reconnect state without exposing message contents.
        }
        finally
        {
            _receiveEnded = true;
            if (!cancellationToken.IsCancellationRequested && !roomClosed)
            {
                Disconnected?.Invoke();
            }
        }
    }

    private void HandleHostAction(JsonElement root)
    {
        var action = root.Deserialize<WatchRoomHostAction>(JsonOptions);
        if (action?.Action is "select" or "play" or "pause" or "seek" or "rate")
        {
            HostActionReceived?.Invoke(action);
        }
    }

    private static WatchRoomChatMessage? ParseChat(JsonElement root)
    {
        var chat = root.Deserialize<WatchRoomChatMessage>(JsonOptions);
        if (chat == null || string.IsNullOrEmpty(chat.MemberId)
            || string.IsNullOrEmpty(chat.Nickname) || chat.Nickname.Length > 24
            || string.IsNullOrEmpty(chat.Text) || chat.Text.Length > 500
            || chat.SentAtUnixMs <= 0)
        {
            return null;
        }

        return chat;
    }

    private static async Task<JsonDocument> ReceiveOneAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new IOException("房间连接已关闭。");
            }

            if (result.MessageType != WebSocketMessageType.Text
                || stream.Length + result.Count > MaxMessageBytes)
            {
                throw new InvalidDataException("房间消息格式或大小无效。");
            }

            await stream.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken)
                .ConfigureAwait(false);
            if (result.EndOfMessage)
            {
                return JsonDocument.Parse(stream.ToArray());
            }
        }
    }

    private async Task CloseSocketAsync()
    {
        if (_receiveCancellation != null)
        {
            await _receiveCancellation.CancelAsync().ConfigureAwait(false);
        }

        _socket?.Dispose();
        if (_receiveTask != null)
        {
            await _receiveTask.ConfigureAwait(false);
        }

        _receiveTask = null;
        _receiveCancellation?.Dispose();
        _receiveCancellation = null;
        _socket = null;
    }

    public async ValueTask DisposeAsync()
    {
        await LeaveAsync().ConfigureAwait(false);
        _sendGate.Dispose();
    }
}

internal sealed record WatchRoomMedia
{
    public long EpisodeId { get; init; }
    public long Aid { get; init; }
    public string? Bvid { get; init; }
    public long Cid { get; init; }
}

internal sealed record WatchRoomChatMessage
{
    public string MemberId { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
    public string Nickname { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public long SentAtUnixMs { get; init; }
}

internal sealed record WatchRoomHostAction
{
    public string Action { get; init; } = string.Empty;
    public double? PositionSeconds { get; init; }
    public double? Rate { get; init; }
}

internal sealed record WatchRoomMember
{
    public string MemberId { get; init; } = string.Empty;
    public bool Online { get; init; }
    public bool Ready { get; init; }
    public bool Buffering { get; init; }
}

internal sealed record WatchRoomSnapshot
{
    public long Version { get; init; }
    public long SyncRevision { get; init; }
    public WatchRoomMedia? Media { get; init; }
    public double PositionSeconds { get; init; }
    public bool Playing { get; init; }
    public double Rate { get; init; } = 1;
    public long ServerTimeUnixMs { get; init; }
    public bool WaitingForReady { get; init; }
    public int MemberCount { get; init; }
    public WatchRoomMember? Host { get; init; }
    public IReadOnlyList<WatchRoomMember> Guests { get; init; } = [];

    public bool AllOnlineGuestsReady => Guests.All(guest => !guest.Online || (guest.Ready && !guest.Buffering));

    public WatchRoomMember? FindMember(string? memberId) => Host?.MemberId == memberId
        ? Host : Guests.FirstOrDefault(guest => guest.MemberId == memberId);
}
