using System;
using System.IO;
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
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private long _lastPingUnixMs;

    public event Action<WatchRoomSnapshot>? SnapshotReceived;
    public event Action? Disconnected;
    public event Action? Closed;
    public event Action<string>? LoginQrReceived;
    public event Action? GuestLoginCompleted;

    public string? RoomCode { get; private set; }
    public string? ClientId { get; private set; }
    public bool IsHost { get; private set; }
    public double ClockOffsetMilliseconds { get; private set; }
    public double RoundTripMilliseconds { get; private set; }
    public bool HasClockEstimate { get; private set; }
    public bool Connected => _socket?.State == WebSocketState.Open;

    public async Task<WatchRoomSnapshot> ConnectAsync(
        string serviceAddress,
        bool create,
        string? roomCode,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(serviceAddress, UriKind.Absolute, out var uri)
            || !((uri.Scheme == "wss") || (uri.Scheme == "ws" && uri.IsLoopback))
            || uri.AbsolutePath != "/ws")
        {
            throw new InvalidOperationException("房间地址须为 wss://…/ws；仅本机可使用 ws://127.0.0.1:端口/ws。");
        }

        var reconnectClientId = !create && string.Equals(roomCode, RoomCode, StringComparison.Ordinal)
            ? ClientId
            : null;
        await CloseSocketAsync().ConfigureAwait(false);
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
        _socket = socket;
        _receiveCancellation = new CancellationTokenSource();
        try
        {
            var requestedRoom = create ? null : roomCode ?? RoomCode;
            if (!create && string.IsNullOrWhiteSpace(requestedRoom))
            {
                throw new InvalidOperationException("请输入房间邀请码。");
            }

            if (create)
            {
                await SendAsync(new { type = "create" }, cancellationToken).ConfigureAwait(false);
            }
            else if (reconnectClientId != null)
            {
                await SendAsync(new { type = "join", roomCode = requestedRoom, clientId = reconnectClientId },
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await SendAsync(new { type = "join", roomCode = requestedRoom }, cancellationToken)
                    .ConfigureAwait(false);
            }
            using var welcome = await ReceiveOneAsync(socket, cancellationToken).ConfigureAwait(false);
            var type = welcome.RootElement.GetProperty("type").GetString();
            if (type != "welcome")
            {
                throw new InvalidOperationException("房间拒绝加入；请检查邀请码和人数。");
            }

            var root = welcome.RootElement;
            RoomCode = root.GetProperty("roomCode").GetString();
            ClientId = root.GetProperty("clientId").GetString();
            IsHost = root.GetProperty("role").GetString() == "host";
            var snapshot = root.GetProperty("snapshot").Deserialize<WatchRoomSnapshot>(JsonOptions)
                           ?? throw new InvalidDataException("房间状态为空。");
            _receiveTask = ReceiveLoopAsync(_receiveCancellation.Token);
            return snapshot;
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

    public async Task LeaveAsync()
    {
        await CloseSocketAsync().ConfigureAwait(false);
        RoomCode = null;
        ClientId = null;
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
                if (TryHandleLoginMessage(root, type))
                {
                    continue;
                }
                if (type == "snapshot")
                {
                    var snapshot = root.GetProperty("snapshot")
                        .Deserialize<WatchRoomSnapshot>(JsonOptions);
                    if (snapshot != null)
                    {
                        SnapshotReceived?.Invoke(snapshot);
                    }
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
                    RoomCode = null;
                    ClientId = null;
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
            if (!cancellationToken.IsCancellationRequested && !roomClosed)
            {
                Disconnected?.Invoke();
            }
        }
    }

    private bool TryHandleLoginMessage(JsonElement root, string? type)
    {
        if (type == "login_qr")
        {
            if (IsHost && root.TryGetProperty("loginUrl", out var url)
                && url.ValueKind == JsonValueKind.String && url.GetString() is { } loginUrl)
            {
                LoginQrReceived?.Invoke(loginUrl);
            }

            return true;
        }

        if (type == "login_done")
        {
            if (IsHost)
            {
                GuestLoginCompleted?.Invoke();
            }

            return true;
        }

        return false;
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

internal sealed record WatchRoomMember
{
    public bool Online { get; init; }
    public bool Ready { get; init; }
    public bool Buffering { get; init; }
}

internal sealed record WatchRoomSnapshot
{
    public long Version { get; init; }
    public WatchRoomMedia? Media { get; init; }
    public double PositionSeconds { get; init; }
    public bool Playing { get; init; }
    public double Rate { get; init; } = 1;
    public long ServerTimeUnixMs { get; init; }
    public bool WaitingForReady { get; init; }
    public WatchRoomMember? Host { get; init; }
    public WatchRoomMember? Guest { get; init; }
}
