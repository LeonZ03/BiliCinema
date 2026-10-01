using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Diagnostics;
using DownKyi.Core.Aria2cNet.Client;
using DownKyi.Core.Aria2cNet.Server;
using DownKyi.Core.Settings;
using DownKyi.Utils;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace DownKyi.Services.Download;

internal sealed class Aria2RuntimeLifecycle : IDisposable
{
    internal const string SecureRedirectFeature = "downkyi-secure-redirect-v2";

    private readonly AriaClient _ariaClient;
    private readonly AriaServer _ariaServer;
    private readonly DownloadDiagnosticLogger _diagnosticLogger;
    private readonly LocalAriaRpcEndpoint? _localEndpoint;
    private readonly ILogger<Aria2RuntimeLifecycle> _logger;
    private readonly NetworkApplicationSettings _networkSettings;
    private readonly bool _ownsAriaServer;
    private readonly Func<IAria2NotificationSocket> _notificationSocketFactory;
    private readonly object _notificationGate = new();
    private readonly Dictionary<string, TaskCompletionSource<Aria2PauseCheckpoint>>
        _pauseWaiters = new(StringComparer.Ordinal);
    private CancellationTokenSource? _notificationCancellation;
    private IAria2NotificationSocket? _notificationSocket;
    private Task _notificationTask = Task.CompletedTask;
    private bool _notificationConnected;

    public Aria2RuntimeLifecycle(
        NetworkApplicationSettings networkSettings,
        AriaClient ariaClient,
        DownloadDiagnosticLogger diagnosticLogger,
        AriaServer ariaServer,
        ILogger<Aria2RuntimeLifecycle> logger,
        bool ownsAriaServer,
        LocalAriaRpcEndpoint? localEndpoint,
        Func<IAria2NotificationSocket>? notificationSocketFactory = null)
    {
        _networkSettings = networkSettings ?? throw new ArgumentNullException(nameof(networkSettings));
        _ariaClient = ariaClient ?? throw new ArgumentNullException(nameof(ariaClient));
        _diagnosticLogger = diagnosticLogger ?? throw new ArgumentNullException(nameof(diagnosticLogger));
        _ariaServer = ariaServer ?? throw new ArgumentNullException(nameof(ariaServer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _ownsAriaServer = ownsAriaServer;
        _localEndpoint = localEndpoint;
        _notificationSocketFactory = notificationSocketFactory
            ?? (() => new ClientWebSocketAria2NotificationSocket());
        if (ownsAriaServer != (localEndpoint != null))
        {
            throw new ArgumentException(
                "A local aria2 endpoint is required only for an owned aria2 process.",
                nameof(localEndpoint));
        }
    }

    public string Name => _ownsAriaServer ? "aria2-local" : "aria2-custom";

    internal bool UsesPauseNotifications => _ownsAriaServer;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_ownsAriaServer)
        {
            await StartOwnedServerAsync(cancellationToken).ConfigureAwait(true);
            await StartNotificationListenerAsync(cancellationToken).ConfigureAwait(true);
        }
        else
        {
            await EnsureSecureRedirectFeatureAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_ownsAriaServer)
        {
            await StopNotificationListenerAsync(cancellationToken).ConfigureAwait(true);
            await CloseOwnedServerAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    public void AbortStartup()
    {
        AbortNotificationListener();
        if (_ownsAriaServer)
        {
            _ariaServer.KillTrackedServer("aria2 runtime startup failed.");
        }
    }

    internal Aria2PauseWaitRegistration RegisterPauseWaiter(string gid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gid);
        if (!UsesPauseNotifications)
        {
            throw new InvalidOperationException(
                "Custom aria2 runtimes do not own a WebSocket notification listener.");
        }

        var completion = new TaskCompletionSource<Aria2PauseCheckpoint>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_notificationGate)
        {
            if (!_notificationConnected)
            {
                completion.TrySetResult(Aria2PauseCheckpoint.Disconnected);
            }
            else if (!_pauseWaiters.TryAdd(gid, completion))
            {
                throw new InvalidOperationException(
                    $"A pause waiter is already registered for aria2 GID '{gid}'.");
            }
        }

        return new Aria2PauseWaitRegistration(this, gid, completion);
    }

    private async Task StartOwnedServerAsync(CancellationToken cancellationToken)
    {
        var endpoint = _localEndpoint
            ?? throw new InvalidOperationException("The local aria2 endpoint is missing.");
        var config = CreateServerConfig(endpoint);
        _diagnosticLogger.LogAriaServerConfig(Name, config, _networkSettings);

        var errors = new ConcurrentQueue<string>();
        await _ariaServer.StartServerAsync(config, output =>
        {
            if (!string.IsNullOrWhiteSpace(output))
            {
                errors.Enqueue(output);
            }
        }).ConfigureAwait(true);

        if (string.Join(Environment.NewLine, errors)
            .Contains("ERROR", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The local aria2 process reported a startup error.");
        }

        HttpRequestException? lastRpcError = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_ariaServer.IsTrackedServerRunning())
            {
                throw new InvalidOperationException(
                    "The supervised aria2 process exited before RPC became ready.");
            }

            try
            {
                var version = await _ariaClient
                    .GetAriaVersionAsync(cancellationToken)
                    .ConfigureAwait(true);
                if (version is { Result: { } versionResult })
                {
                    EnsureSecureRedirectFeature(versionResult.EnabledFeatures);
                    _ariaServer.ReleaseStartupSecrets();
                    return;
                }
            }
            catch (HttpRequestException exception)
            {
                lastRpcError = exception;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(true);
        }

        throw new TimeoutException(
            "The local aria2 process did not accept RPC requests in time.",
            lastRpcError);
    }

    private AriaConfig CreateServerConfig(LocalAriaRpcEndpoint endpoint)
    {
        return new AriaConfig
        {
            ListenPort = endpoint.Port,
            Token = endpoint.Secret,
            LogLevel = _networkSettings.AriaLogLevel,
            MaxConcurrentDownloads = _networkSettings.MaxCurrentDownloads,
            MaxConnectionPerServer = _networkSettings.AriaMaxConnectionPerServer,
            Split = _networkSettings.AriaSplit,
            MinSplitSize = _networkSettings.AriaMinSplitSize,
            MaxOverallDownloadLimit = _networkSettings.AriaMaxOverallDownloadLimit * 1024L,
            MaxDownloadLimit = _networkSettings.AriaMaxDownloadLimit * 1024L,
            ContinueDownload = true,
            FileAllocation = _networkSettings.AriaFileAllocation
        };
    }

    private async Task EnsureSecureRedirectFeatureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var version = await _ariaClient
            .GetAriaVersionAsync(cancellationToken)
            .ConfigureAwait(true);
        if (version is not { Result: { } versionResult })
        {
            throw new InvalidOperationException(
                "The aria2 endpoint did not return its security capabilities.");
        }

        EnsureSecureRedirectFeature(versionResult.EnabledFeatures);
    }

    private async Task StartNotificationListenerAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_notificationGate)
        {
            if (_notificationSocket != null)
            {
                return;
            }
        }

        var socket = _notificationSocketFactory();
        var listenerCancellation = new CancellationTokenSource();
        try
        {
            await socket.ConnectAsync(_ariaClient.WebSocketUri, cancellationToken)
                .ConfigureAwait(true);
            lock (_notificationGate)
            {
                _notificationSocket = socket;
                _notificationCancellation = listenerCancellation;
                _notificationConnected = true;
                _notificationTask = ReceiveNotificationsAsync(listenerCancellation.Token);
            }
        }
        catch
        {
            listenerCancellation.Dispose();
            socket.Dispose();
            throw;
        }
    }

    private async Task ReceiveNotificationsAsync(CancellationToken cancellationToken)
    {
        var socket = _notificationSocket
            ?? throw new InvalidOperationException("The aria2 WebSocket is not initialized.");
        var buffer = new byte[4096];
        using var message = new MemoryStream();
        try
        {
            while (true)
            {
                var received = await socket
                    .ReceiveAsync(buffer, cancellationToken)
                    .ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogWarningMessage(
                        "aria2 pause communication ended; " +
                        "layer=websocket; operation=receive; close=remote.");
                    return;
                }

                if (received.MessageType != WebSocketMessageType.Text)
                {
                    if (received.EndOfMessage)
                    {
                        message.SetLength(0);
                    }

                    continue;
                }

                await message.WriteAsync(
                    buffer.AsMemory(0, received.Count),
                    cancellationToken).ConfigureAwait(false);
                if (message.Length > 64 * 1024)
                {
                    throw new InvalidDataException(
                        "aria2 WebSocket notification exceeded the supported size.");
                }

                if (!received.EndOfMessage)
                {
                    continue;
                }

                var payload = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                HandleNotification(payload);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (exception is WebSocketException
            or IOException
            or ObjectDisposedException)
        {
            _logger.LogWarningMessage(
                "aria2 pause communication failed; " +
                $"layer=websocket; operation=receive; type={exception.GetType().Name}.",
                exception);
        }
        finally
        {
            TaskCompletionSource<Aria2PauseCheckpoint>[] waiters;
            lock (_notificationGate)
            {
                waiters = DisconnectAndTakePauseWaitersLocked();
            }

            CompletePauseWaiters(waiters, Aria2PauseCheckpoint.Disconnected);
        }
    }

    private void HandleNotification(string payload)
    {
        JObject notification;
        try
        {
            notification = JObject.Parse(payload);
        }
        catch (Newtonsoft.Json.JsonException exception)
        {
            _logger.LogWarningMessage(
                "aria2 pause communication failed; " +
                $"layer=websocket; operation=parse; type={exception.GetType().Name}.",
                exception);
            return;
        }

        var checkpoint = notification["method"]?.Value<string>() switch
        {
            "aria2.onDownloadPause" => Aria2PauseCheckpoint.Paused,
            "aria2.onDownloadComplete" => Aria2PauseCheckpoint.Complete,
            "aria2.onDownloadError" => Aria2PauseCheckpoint.Error,
            "aria2.onDownloadStop" => Aria2PauseCheckpoint.Removed,
            _ => (Aria2PauseCheckpoint?)null
        };
        var gid = notification["params"]?[0]?["gid"]?.Value<string>();
        if (checkpoint == null || string.IsNullOrWhiteSpace(gid))
        {
            return;
        }

        TaskCompletionSource<Aria2PauseCheckpoint>? waiter;
        lock (_notificationGate)
        {
            _pauseWaiters.Remove(gid, out waiter);
        }

        waiter?.TrySetResult(checkpoint.Value);
    }

    private async Task StopNotificationListenerAsync(CancellationToken cancellationToken)
    {
        IAria2NotificationSocket? socket;
        CancellationTokenSource? listenerCancellation;
        Task listenerTask;
        TaskCompletionSource<Aria2PauseCheckpoint>[] waiters;
        lock (_notificationGate)
        {
            socket = _notificationSocket;
            listenerCancellation = _notificationCancellation;
            listenerTask = _notificationTask;
            waiters = DisconnectAndTakePauseWaitersLocked();
        }

        CompletePauseWaiters(waiters, Aria2PauseCheckpoint.Disconnected);
        if (socket == null || listenerCancellation == null)
        {
            return;
        }

        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "DownKyi aria2 runtime stopped.",
                    cancellationToken).ConfigureAwait(true);
            }
        }
        catch (Exception exception) when (exception is WebSocketException or IOException)
        {
            _logger.LogWarningMessage(
                "aria2 pause communication failed; " +
                $"layer=websocket; operation=close; type={exception.GetType().Name}.",
                exception);
        }
        finally
        {
            await listenerCancellation.CancelAsync().ConfigureAwait(true);
            await listenerTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            socket.Dispose();
            listenerCancellation.Dispose();
            lock (_notificationGate)
            {
                if (ReferenceEquals(_notificationSocket, socket))
                {
                    _notificationSocket = null;
                    _notificationCancellation = null;
                    _notificationTask = Task.CompletedTask;
                }
            }
        }
    }

    private void AbortNotificationListener()
    {
        IAria2NotificationSocket? socket;
        CancellationTokenSource? listenerCancellation;
        TaskCompletionSource<Aria2PauseCheckpoint>[] waiters;
        lock (_notificationGate)
        {
            socket = _notificationSocket;
            listenerCancellation = _notificationCancellation;
            _notificationSocket = null;
            _notificationCancellation = null;
            waiters = DisconnectAndTakePauseWaitersLocked();
        }

        listenerCancellation?.Cancel();
        socket?.Dispose();
        listenerCancellation?.Dispose();
        CompletePauseWaiters(waiters, Aria2PauseCheckpoint.Disconnected);
    }

    private TaskCompletionSource<Aria2PauseCheckpoint>[]
        DisconnectAndTakePauseWaitersLocked()
    {
        _notificationConnected = false;
        TaskCompletionSource<Aria2PauseCheckpoint>[] waiters = [.. _pauseWaiters.Values];
        _pauseWaiters.Clear();
        return waiters;
    }

    private static void CompletePauseWaiters(
        IEnumerable<TaskCompletionSource<Aria2PauseCheckpoint>> waiters,
        Aria2PauseCheckpoint checkpoint)
    {
        foreach (var waiter in waiters)
        {
            waiter.TrySetResult(checkpoint);
        }
    }

    private void RemovePauseWaiter(
        string gid,
        TaskCompletionSource<Aria2PauseCheckpoint> completion)
    {
        lock (_notificationGate)
        {
            if (_pauseWaiters.TryGetValue(gid, out var current)
                && ReferenceEquals(current, completion))
            {
                _pauseWaiters.Remove(gid);
            }
        }
    }

    private static void EnsureSecureRedirectFeature(IReadOnlyList<string> features)
    {
        if (!features.Contains(SecureRedirectFeature, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "The aria2 endpoint is incompatible with DownKyi downloads: " +
                "aria2.getVersion().enabledFeatures does not include the required " +
                $"'{SecureRedirectFeature}' capability. Standard aria2 and Motrix do not " +
                "provide this DownKyi-specific redirect protection.");
        }
    }

    private async Task CloseOwnedServerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _ariaClient.PauseAllAsync()
                .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is TimeoutException
            or HttpRequestException
            or IOException
            or InvalidOperationException
            or Newtonsoft.Json.JsonException)
        {
            _logger.LogErrorMessage("Aria server shutdown failed.", exception);
        }

        if (!await _ariaServer.CloseServerAsync(
                _ariaClient,
                TimeSpan.FromSeconds(3)).ConfigureAwait(true))
        {
            await _ariaServer.ForceCloseServerAsync(
                _ariaClient,
                TimeSpan.FromSeconds(2)).ConfigureAwait(true);
        }
    }

    public void Dispose()
    {
        AbortNotificationListener();
        if (_ownsAriaServer)
        {
            _ariaServer.KillTrackedServer(
                "aria2 runtime disposed before graceful shutdown completed.");
        }
    }

    internal sealed class Aria2PauseWaitRegistration : IDisposable
    {
        private readonly Aria2RuntimeLifecycle _owner;
        private readonly string _gid;
        private readonly TaskCompletionSource<Aria2PauseCheckpoint> _completion;
        private int _disposed;

        public Aria2PauseWaitRegistration(
            Aria2RuntimeLifecycle owner,
            string gid,
            TaskCompletionSource<Aria2PauseCheckpoint> completion)
        {
            _owner = owner;
            _gid = gid;
            _completion = completion;
        }

        public Task<Aria2PauseCheckpoint> Completion => _completion.Task;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.RemovePauseWaiter(_gid, _completion);
            }
        }
    }

    internal enum Aria2PauseCheckpoint
    {
        Paused,
        Complete,
        Error,
        Removed,
        Disconnected
    }
}

internal interface IAria2NotificationSocket : IDisposable
{
    WebSocketState State { get; }

    Task ConnectAsync(Uri uri, CancellationToken cancellationToken);

    ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken);

    Task CloseOutputAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken);
}

internal sealed class ClientWebSocketAria2NotificationSocket : IAria2NotificationSocket
{
    private readonly ClientWebSocket _socket = new();

    public WebSocketState State => _socket.State;

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) =>
        _socket.ConnectAsync(uri, cancellationToken);

    public ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken) =>
        _socket.ReceiveAsync(buffer, cancellationToken);

    public Task CloseOutputAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken) =>
        _socket.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

    public void Dispose() => _socket.Dispose();
}
