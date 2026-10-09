using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using DownKyi.Core.Aria2cNet.Server;

namespace DownKyi.Services.Watch;

internal sealed record RoomCreationProgress(int Percentage, string Step);

internal sealed class RoomGatewayTunnel : IAsyncDisposable
{
    private const string GatewayOrigin = "https://bilicinema.leonz03.dpdns.org";
    private const string GatewayWebSocket = "wss://bilicinema.leonz03.dpdns.org/ws";
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    // Newly provisioned Tunnel routes can differ between edge connections.
    // Bound connection reuse so a failed edge route cannot pin every retry.
    private readonly HttpClient _client = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromSeconds(5),
        UseCookies = false,
    })
    { Timeout = TimeSpan.FromSeconds(20) };
    private string _operationId = NewSecret();
    private string _sessionSecret = NewSecret();
    private string _routeKey = NewSecret();
    private readonly HashSet<string> _rooms = new(StringComparer.Ordinal);
    private readonly object _roomsGate = new();
    private Process? _process;
    private WindowsProcessJob? _processJob;
    private string? _sessionId;
    private string? _tunnelToken;
    private volatile bool _connectorRegistered;
    private TaskCompletionSource? _connectorRegistration;
    private CancellationTokenSource? _heartbeatCancellation;
    private Task? _heartbeatTask;

    public static string ServiceAddress => GatewayWebSocket;
    public string LocalRouteKey => _routeKey;
    public string? InviteAddress { get; private set; }
    public bool IsRunning => _process is { HasExited: false };
    internal string? OriginHost { get; private set; }

    public async Task<string> StartAsync(string roomCode, IProgress<RoomCreationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(roomCode))
        {
            throw new ArgumentException("房间号不能为空。", nameof(roomCode));
        }

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Retain cleanup ownership even when invitation preparation fails.
            lock (_roomsGate) _rooms.Add(roomCode);
            await PrepareGatewayAsync(progress, cancellationToken).ConfigureAwait(false);
            await RegisterRoomAsync(roomCode, cancellationToken).ConfigureAwait(false);
            await ProbeRoomAsync(roomCode, cancellationToken).ConfigureAwait(false);
            InviteAddress = $"{GatewayWebSocket}?room={Uri.EscapeDataString(roomCode)}";
            StartHeartbeat();
            progress?.Report(new(85, "固定域名公网通道已就绪"));
            return GatewayWebSocket;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task UnregisterRoomAsync(string roomCode, CancellationToken cancellationToken = default)
    {
        if (_heartbeatCancellation is { } heartbeat)
            await heartbeat.CancelAsync().ConfigureAwait(false);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopHeartbeatCoreAsync().ConfigureAwait(false);
            lock (_roomsGate) _rooms.Remove(roomCode);
            if (_sessionId != null)
            {
                await SendBestEffortAsync(HttpMethod.Delete,
                    $"/api/v1/hosts/{Uri.EscapeDataString(_sessionId)}/rooms/{Uri.EscapeDataString(roomCode)}",
                    null, cancellationToken).ConfigureAwait(false);
            }

            bool noRooms;
            lock (_roomsGate) noRooms = _rooms.Count == 0;
            if (noRooms)
            {
                InviteAddress = null;
            }
            else StartHeartbeat();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync()
    {
        // Cancel recovery before waiting for the gate it may be holding.
        if (_heartbeatCancellation is { } heartbeat)
            await heartbeat.CancelAsync().ConfigureAwait(false);
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task PrepareGatewayAsync(IProgress<RoomCreationProgress>? progress,
        CancellationToken cancellationToken)
    {
        for (var recovery = 0; ; recovery++)
        {
            try
            {
                await EnsureSessionAsync(progress, cancellationToken).ConfigureAwait(false);
                await EnsureTunnelAsync(progress, cancellationToken).ConfigureAwait(false);
                await WaitUntilReadyAsync(progress, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (HttpRequestException error) when (recovery == 0 && error.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Renew the expired cloud lease without changing the key used
                // by the still-running local room server.
                await ResetSessionAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task ResetSessionAsync()
    {
        await StopProcessCoreAsync().ConfigureAwait(false);
        _sessionId = null;
        _tunnelToken = null;
        OriginHost = null;
        _operationId = NewSecret();
        _sessionSecret = NewSecret();
    }

    private async Task EnsureSessionAsync(IProgress<RoomCreationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (_sessionId != null && !string.IsNullOrWhiteSpace(_tunnelToken))
        {
            return;
        }

        progress?.Report(new(30, "申请房主公网会话"));
        var result = await SendAsync<HostSessionResponse>(HttpMethod.Post, "/api/v1/hosts/session",
            new
            {
                operationId = _operationId,
                sessionSecret = _sessionSecret,
                routeKey = _routeKey,
            }, cancellationToken).ConfigureAwait(false);
        _sessionId = result.SessionId;
        _tunnelToken = result.TunnelToken;
        OriginHost = result.OriginHost;
        progress?.Report(new(42, "主机会话已准备，启动固定 Tunnel"));
    }

    private async Task EnsureTunnelAsync(IProgress<RoomCreationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (IsRunning)
        {
            return;
        }

        if (_process is { HasExited: true })
        {
            await StopProcessCoreAsync().ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(_tunnelToken))
        {
            throw new InvalidOperationException("房主 Tunnel 凭据尚未准备好，请重试创建房间。");
        }

        var adjacentExecutable = Path.Combine(AppContext.BaseDirectory, "cloudflared.exe");
        var bundledExecutable = await Task.Run(BundledTools.EnsureTunnelTool, cancellationToken)
            .ConfigureAwait(false);
        var start = new ProcessStartInfo(bundledExecutable ?? (File.Exists(adjacentExecutable)
            ? adjacentExecutable : "cloudflared.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("tunnel");
        start.ArgumentList.Add("--no-autoupdate");
        start.ArgumentList.Add("--protocol");
        start.ArgumentList.Add("http2");
        start.ArgumentList.Add("run");
        start.Environment["TUNNEL_TOKEN"] = _tunnelToken;

        try
        {
            _process = new Process { StartInfo = start, EnableRaisingEvents = true };
            _connectorRegistered = false;
            var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _connectorRegistration = registered;
            void ObserveConnector(object sender, DataReceivedEventArgs args)
            {
                if (args.Data?.Contains("Registered tunnel connection", StringComparison.Ordinal) == true)
                {
                    _connectorRegistered = true;
                    registered.TrySetResult();
                }
            }
            _process.OutputDataReceived += ObserveConnector;
            _process.ErrorDataReceived += ObserveConnector;
            _process.Exited += (_, _) => registered.TrySetResult();
            if (!_process.Start())
            {
                throw new InvalidOperationException("无法启动 Cloudflare Tunnel。");
            }

            if (OperatingSystem.IsWindows())
                _processJob = WindowsProcessJob.CreateAndAssign(_process);
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            progress?.Report(new(55, "等待固定 Tunnel 注册"));
        }
        catch (Win32Exception error)
        {
            await StopProcessCoreAsync().ConfigureAwait(false);
            throw new InvalidOperationException("无法启动或管理 Cloudflare Tunnel，请重试创建房间。", error);
        }
    }

    private async Task WaitUntilReadyAsync(IProgress<RoomCreationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (_sessionId == null)
        {
            throw new InvalidOperationException("房主公网会话不存在。");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ReadyTimeout);
        var attempt = 0;
        Exception? lastFailure = null;
        try
        {
            if (_connectorRegistration != null)
                await _connectorRegistration.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (!IsRunning)
                {
                    throw new InvalidOperationException("Cloudflare Tunnel 已退出，请检查网络后重试。");
                }

                attempt++;
                try
                {
                    await SendAsync(HttpMethod.Get, $"/api/v1/hosts/{Uri.EscapeDataString(_sessionId)}/ready",
                        null, deadline.Token).ConfigureAwait(false);
                    progress?.Report(new(70, $"固定 Tunnel 已连通 · 第 {attempt} 次检测"));
                    return;
                }
                catch (Exception error) when (IsRetryable(error, deadline.Token))
                {
                    lastFailure = error;
                    progress?.Report(new(65, $"等待固定 Tunnel 就绪 · 第 {attempt} 次检测"));
                }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested)
                {
                    lastFailure = new TimeoutException("公网连接暂时超时，正在重试。");
                }

                await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, attempt)), deadline.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            var connectorState = _connectorRegistered ? "Tunnel 已注册。" : "Tunnel 尚未注册。";
            throw new TimeoutException("固定 Tunnel 就绪检测超时。" + connectorState + lastFailure?.Message, lastFailure);
        }
    }

    private static Task ProbeRoomAsync(string roomCode, CancellationToken cancellationToken) =>
        ProbePublicRouteAsync(new Uri($"{GatewayWebSocket}?room={Uri.EscapeDataString(roomCode)}"), cancellationToken);

    internal static async Task ProbePublicRouteAsync(Uri publicAddress, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var address = new UriBuilder(publicAddress)
        {
            Scheme = publicAddress.Scheme is "https" or "wss" ? "wss" : "ws",
            Path = "/ws",
        }.Uri;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                using var socket = new ClientWebSocket();
                socket.Options.CollectHttpResponseDetails = true;
                try
                {
                    await socket.ConnectAsync(address, deadline.Token).ConfigureAwait(false);
                    socket.Abort();
                    return;
                }
                catch (WebSocketException error)
                {
                    var status = socket.HttpStatusCode;
                    var failure = new HttpRequestException(
                        $"WebSocket 通道暂不可用（HTTP {(int)status}）。", error,
                        status == 0 ? null : status);
                    if (attempt >= 2 || !IsRetryable(failure, deadline.Token)) throw failure;
                }
                await Task.Delay(TimeSpan.FromSeconds(attempt + 1), deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("固定域名 WebSocket 连接验证超时，请重试公网邀请。");
        }
    }

    private async Task RegisterRoomAsync(string roomCode, CancellationToken cancellationToken)
    {
        if (_sessionId == null)
        {
            throw new InvalidOperationException("房主公网会话不存在。");
        }

        await SendAsync(HttpMethod.Post,
            $"/api/v1/hosts/{Uri.EscapeDataString(_sessionId)}/rooms/{Uri.EscapeDataString(roomCode)}",
            new { }, cancellationToken).ConfigureAwait(false);
    }

    private void StartHeartbeat()
    {
        if (_heartbeatTask is { IsCompleted: false } || _sessionId == null)
        {
            return;
        }

        _heartbeatCancellation?.Dispose();
        _heartbeatCancellation = new CancellationTokenSource();
        _heartbeatTask = HeartbeatAsync(_heartbeatCancellation.Token);
    }

    private async Task HeartbeatAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(HeartbeatInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    try
                    {
                        if (_sessionId == null || !IsRunning)
                            await RecoverRoutesAsync(cancellationToken).ConfigureAwait(false);
                        await SendAsync(HttpMethod.Post,
                            $"/api/v1/hosts/{Uri.EscapeDataString(_sessionId!)}/heartbeat",
                            new { roomCodes = SnapshotRooms() }, cancellationToken).ConfigureAwait(false);
                    }
                    catch (HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        await ResetSessionAsync().ConfigureAwait(false);
                        await RecoverRoutesAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // A request timed out; the next tick retries within the lease.
                }
                catch (Exception error) when (error is HttpRequestException or InvalidOperationException or JsonException or TimeoutException)
                {
                    // The server-side expiry is authoritative. A later tick can
                    // recover after a short network interruption.
                }
                finally { _lifecycleGate.Release(); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task RecoverRoutesAsync(CancellationToken cancellationToken)
    {
        await PrepareGatewayAsync(null, cancellationToken).ConfigureAwait(false);
        foreach (var roomCode in SnapshotRooms())
        {
            await RegisterRoomAsync(roomCode, cancellationToken).ConfigureAwait(false);
            await ProbeRoomAsync(roomCode, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task StopCoreAsync()
    {
        await StopHeartbeatCoreAsync().ConfigureAwait(false);

        if (_sessionId != null)
        {
            foreach (var roomCode in SnapshotRooms())
            {
                await SendBestEffortAsync(HttpMethod.Delete,
                    $"/api/v1/hosts/{Uri.EscapeDataString(_sessionId)}/rooms/{Uri.EscapeDataString(roomCode)}",
                    null, CancellationToken.None).ConfigureAwait(false);
            }
        }

        lock (_roomsGate) _rooms.Clear();
        InviteAddress = null;
        await ResetSessionAsync().ConfigureAwait(false);
        _routeKey = NewSecret();
    }

    private async Task StopHeartbeatCoreAsync()
    {
        if (_heartbeatCancellation != null)
        {
            await _heartbeatCancellation.CancelAsync().ConfigureAwait(false);
            _heartbeatCancellation.Dispose();
            _heartbeatCancellation = null;
        }

        if (_heartbeatTask != null)
        {
            try { await _heartbeatTask.ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                // Shutdown has deliberately cancelled the heartbeat owner.
            }
            _heartbeatTask = null;
        }
    }

    private async Task StopProcessCoreAsync()
    {
        var process = _process;
        _process = null;
        _processJob?.Dispose();
        _processJob = null;
        if (process == null) return;

        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Termination was already requested through the lifetime job and
            // Kill; only waiting for the OS to report exit exceeded its budget.
        }
        catch (InvalidOperationException)
        {
            // The process exited before termination, or failed to start.
        }
        finally { process.Dispose(); }
    }

    private string[] SnapshotRooms()
    {
        lock (_roomsGate) return _rooms.ToArray();
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, GatewayOrigin + path);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_sessionSecret}");
        if (body != null) request.Content = JsonContent.Create(body);
        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<GatewayErrorEnvelope>(cancellationToken)
                .ConfigureAwait(false);
            throw CreateGatewayException(response.StatusCode, error?.Message, error?.Error);
        }

        return (await response.Content.ReadFromJsonAsync<T>(cancellationToken).ConfigureAwait(false))
            ?? throw new InvalidOperationException("固定域名房间服务返回了空响应。");
    }

    private async Task SendAsync(HttpMethod method, string path, object? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, GatewayOrigin + path);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_sessionSecret}");
        if (body != null) request.Content = JsonContent.Create(body);
        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var payload = await response.Content.ReadFromJsonAsync<GatewayErrorEnvelope>(cancellationToken)
                .ConfigureAwait(false);
            throw CreateGatewayException(response.StatusCode, payload?.Message, payload?.Error);
        }
    }

    private async Task SendBestEffortAsync(HttpMethod method, string path, object? body,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try { await SendAsync(method, path, body, deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The durable cloud cleanup owner retries after local shutdown.
        }
        catch (Exception error) when (error is HttpRequestException or InvalidOperationException or JsonException)
        {
            // The server-side expiry remains the cleanup authority.
        }
    }

    private static HttpRequestException CreateGatewayException(
        System.Net.HttpStatusCode status, string? message, string? code) =>
        new(message ?? (code switch
        {
            "host_capacity" => "公网房间服务暂时达到容量上限，请稍后重试。",
            "tunnel_not_ready" => "固定 Tunnel 尚未就绪。",
            "host_tunnel_unavailable" => "房主公网通道暂时不可用。",
            _ => $"固定域名房间服务不可用（HTTP {(int)status}）。",
        }), null, status);

    private static bool IsRetryable(Exception error, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested && error is HttpRequestException request
        && (request.StatusCode == null || (int)request.StatusCode >= 500
            || request.StatusCode is System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.TooManyRequests);

    private static string NewSecret() => Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync().ConfigureAwait(false); }
        finally
        {
            // Release the connector even if cloud deregistration failed.
            try { await StopProcessCoreAsync().ConfigureAwait(false); }
            finally
            {
                _client.Dispose();
                _lifecycleGate.Dispose();
            }
        }
    }

    private sealed record GatewayErrorEnvelope(
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("message")] string? Message);

    private sealed record HostSessionResponse(
        [property: JsonPropertyName("sessionId")] string SessionId,
        [property: JsonPropertyName("tunnelToken")] string TunnelToken,
        [property: JsonPropertyName("originHost")] string OriginHost,
        [property: JsonPropertyName("expiresAt")] long ExpiresAt,
        [property: JsonPropertyName("gateway")] string Gateway);
}
