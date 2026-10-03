using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Diagnostics;
using DownKyi.Core.Aria2cNet;
using DownKyi.Core.Aria2cNet.Client;
using DownKyi.Core.Aria2cNet.Client.Entity;
using DownKyi.Core.Aria2cNet.Server;
using DownKyi.Core.BiliApi.Login;
using DownKyi.Core.Settings;
using DownKyi.Core.Utils;
using DownKyi.Domain.Downloads;
using DownKyi.Models;
using DownKyi.Utils;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal sealed partial class Aria2TransferBackend : ITransferBackend
{
    private readonly AriaClient _ariaClient;
    private readonly AriaRuntimeClientRegistry _clientRegistry;
    private readonly DownloadDiagnosticLogger _diagnosticLogger;
    private readonly AriaDownloadAddressResolver _addressResolver;
    private readonly Aria2RuntimeLifecycle _runtimeLifecycle;
    private readonly Uri? _httpsProxyAddress;
    private readonly ILoggerFactory _loggerFactory;
    private readonly NetworkApplicationSettings _networkSettings;
    private readonly ILogger<Aria2TransferBackend> _logger;
    private IDisposable? _runtimeRegistration;

    public Aria2TransferBackend(
        NetworkApplicationSettings networkSettings,
        AriaClient ariaClient,
        AriaRuntimeClientRegistry clientRegistry,
        DownloadDiagnosticLogger diagnosticLogger,
        AriaServer ariaServer,
        ILoggerFactory loggerFactory,
        ILogger<Aria2TransferBackend> logger,
        bool ownsAriaServer,
        LocalAriaRpcEndpoint? localEndpoint,
        Func<IAria2NotificationSocket>? notificationSocketFactory = null)
    {
        _networkSettings = networkSettings ?? throw new ArgumentNullException(nameof(networkSettings));
        _ariaClient = ariaClient ?? throw new ArgumentNullException(nameof(ariaClient));
        _clientRegistry = clientRegistry ?? throw new ArgumentNullException(nameof(clientRegistry));
        _diagnosticLogger = diagnosticLogger ?? throw new ArgumentNullException(nameof(diagnosticLogger));
        _httpsProxyAddress = ResolveHttpsProxyAddress(networkSettings);
        _addressResolver = AriaDownloadAddressResolver.Create(_httpsProxyAddress);
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _runtimeLifecycle = new Aria2RuntimeLifecycle(
            networkSettings,
            ariaClient,
            diagnosticLogger,
            ariaServer,
            loggerFactory.CreateLogger<Aria2RuntimeLifecycle>(),
            ownsAriaServer,
            localEndpoint,
            notificationSocketFactory);
    }

    public string Name => _runtimeLifecycle.Name;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _runtimeRegistration ??= _clientRegistry.Activate(_ariaClient);
        try
        {
            await _runtimeLifecycle.StartAsync(cancellationToken).ConfigureAwait(true);
        }
        catch
        {
            _runtimeLifecycle.AbortStartup();
            ReleaseRuntimeRegistration();
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _runtimeLifecycle.StopAsync(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            ReleaseRuntimeRegistration();
        }
    }

    public async Task<DownloadTransferResult> TransferAsync(DownloadTransferRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Urls.Count != 1)
        {
            return DownloadTransferResult.Failed(
                DownloadTransferFailureKind.Permanent,
                "download.transfer.single-address-required");
        }

        string? activeGid;
        try
        {
            var preparation = await EnsureAriaTaskAsync(
                request).ConfigureAwait(true);
            if (preparation.ErrorCode != null)
            {
                _logger.LogWarningMessage(
                    $"aria2 download address was rejected; code={preparation.ErrorCode}");
                return DownloadTransferResult.Failed(
                    DownloadTransferFailureKind.CandidateRejected,
                    preparation.ErrorCode);
            }

            activeGid = preparation.Gid
                ?? throw new InvalidOperationException("The prepared aria2 task identifier is missing.");
        }
        catch (HttpRequestException exception) when (
            TlsFailureClassifier.TryClassify(exception, out var tlsErrorCode))
        {
            _logger.LogWarningMessage(
                $"aria2 address validation failed TLS policy; code={tlsErrorCode}");
            return DownloadTransferResult.Failed(
                DownloadTransferFailureKind.Tls,
                tlsErrorCode);
        }
        catch (Exception exception) when (exception is HttpRequestException
            or IOException
            or TimeoutException)
        {
            _logger.LogWarningMessage(
                $"aria2 RPC transport failed; type={exception.GetType().Name}");
            return DownloadTransferResult.Failed(
                DownloadTransferFailureKind.TransientNetwork,
                "download.transfer.aria2-rpc");
        }
        catch (Newtonsoft.Json.JsonException exception)
        {
            _logger.LogWarningMessage(
                $"aria2 RPC contract failed; type={exception.GetType().Name}");
            return DownloadTransferResult.Failed(
                DownloadTransferFailureKind.Permanent,
                "download.transfer.aria2-rpc-contract");
        }
        catch (InvalidOperationException exception)
        {
            _logger.LogWarningMessage(
                $"aria2 RPC request was rejected; type={exception.GetType().Name}");
            return DownloadTransferResult.Failed(
                DownloadTransferFailureKind.Permanent,
                "download.transfer.aria2-rpc-rejected");
        }

        _diagnosticLogger.LogAriaTaskStart(Name, activeGid, request.Urls.Count, _networkSettings);
        var ariaManager = new AriaManager(
            _ariaClient,
            _loggerFactory.CreateLogger<AriaManager>());
        DownloadProgress? lastProgress = null;
        EventHandler<AriaProgressEventArgs> progressHandler = (_, eventArgs) =>
        {
            var progress = CreateProgress(activeGid, eventArgs);
            if (progress != null)
            {
                lastProgress = progress;
                request.PublishProgress(progress);
            }
        };
        ariaManager.TellStatus += progressHandler;
        try
        {
            if (!_runtimeLifecycle.UsesPauseNotifications)
            {
                var customStatus = await ariaManager.GetDownloadStatusDetailAsync(
                    activeGid,
                    async cancellationToken =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (request.IsPauseRequested())
                        {
                            await _ariaClient.PauseAsync(activeGid, cancellationToken)
                                .ConfigureAwait(false);
                            throw new OperationCanceledException("Download was paused.");
                        }

                        request.EnsureActive();
                    },
                    request.CancellationToken).ConfigureAwait(true);
                return await CreateTransferResultAsync(
                    customStatus,
                    request).ConfigureAwait(true);
            }

            return await TransferWithPauseNotificationsAsync(
                ariaManager,
                activeGid,
                request).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (
            !_runtimeLifecycle.UsesPauseNotifications
            && !request.CancellationToken.IsCancellationRequested
            && request.IsPauseRequested())
        {
            return DownloadTransferResult.Paused();
        }
        catch (Exception exception) when (exception is HttpRequestException
            or IOException
            or TimeoutException)
        {
            _logger.LogWarningMessage(
                $"aria2 RPC polling failed; type={exception.GetType().Name}");
            return DownloadTransferResult.Failed(
                DownloadTransferFailureKind.TransientNetwork,
                "download.transfer.aria2-rpc");
        }
        catch (Newtonsoft.Json.JsonException exception)
        {
            _logger.LogWarningMessage(
                $"aria2 RPC polling contract failed; type={exception.GetType().Name}");
            return DownloadTransferResult.Failed(
                DownloadTransferFailureKind.Permanent,
                "download.transfer.aria2-rpc-contract");
        }
        finally
        {
            ariaManager.TellStatus -= progressHandler;
            if (lastProgress != null)
            {
                await request.PersistProgressAsync(lastProgress, CancellationToken.None)
                    .ConfigureAwait(true);
            }
        }
    }

    private async Task<DownloadTransferResult> TransferWithPauseNotificationsAsync(
        AriaManager ariaManager,
        string activeGid,
        DownloadTransferRequest request)
    {
        using var transferCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            request.CancellationToken);
        Task<AriaDownloadStatus>? statusTask = null;
        Task? pauseRequestTask = null;
        Task<Aria2RuntimeLifecycle.Aria2PauseCheckpoint>? pauseCheckpointTask = null;
        try
        {
            statusTask = ariaManager.GetDownloadStatusDetailAsync(
                activeGid,
                cancellationToken =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!request.IsPauseRequested())
                    {
                        request.EnsureActive();
                    }

                    return ValueTask.CompletedTask;
                },
                transferCancellation.Token);
            pauseRequestTask = request.WaitForPauseRequestedAsync(
                transferCancellation.Token);

            var completedTask = await Task.WhenAny(statusTask, pauseRequestTask)
                .ConfigureAwait(true);
            if (ReferenceEquals(completedTask, statusTask))
            {
                return await CreateTransferResultAsync(
                    await statusTask.ConfigureAwait(true),
                    request).ConfigureAwait(true);
            }

            await pauseRequestTask.ConfigureAwait(true);
            request.CancellationToken.ThrowIfCancellationRequested();
            pauseCheckpointTask = PauseAndWaitForCheckpointAsync(
                activeGid,
                transferCancellation.Token);
            completedTask = await Task.WhenAny(statusTask, pauseCheckpointTask)
                .ConfigureAwait(true);
            if (ReferenceEquals(completedTask, statusTask))
            {
                return await CreateTransferResultAsync(
                    await statusTask.ConfigureAwait(true),
                    request).ConfigureAwait(true);
            }

            Aria2RuntimeLifecycle.Aria2PauseCheckpoint checkpoint;
            try
            {
                checkpoint = await pauseCheckpointTask.ConfigureAwait(true);
            }
            catch (Exception exception) when (IsPauseCommunicationFailure(exception))
            {
                LogPauseCommunicationFailure(
                    "pause-rpc",
                    exception,
                    pauseConfirmed: false);
                var statusAfterPauseFailure = await CancelAndObserveStatusAsync(
                    transferCancellation,
                    statusTask,
                    pauseConfirmed: false).ConfigureAwait(true);
                if (statusAfterPauseFailure != null)
                {
                    return await CreateTransferResultAsync(statusAfterPauseFailure, request)
                        .ConfigureAwait(true);
                }

                return await ResolveUnconfirmedPauseAsync(
                    ariaManager,
                    activeGid,
                    request,
                    triggerLayer: "pause-rpc").ConfigureAwait(true);
            }

            if (checkpoint is Aria2RuntimeLifecycle.Aria2PauseCheckpoint.Complete
                or Aria2RuntimeLifecycle.Aria2PauseCheckpoint.Error
                or Aria2RuntimeLifecycle.Aria2PauseCheckpoint.Removed)
            {
                return await CreateTransferResultAsync(
                    await statusTask.ConfigureAwait(true),
                    request).ConfigureAwait(true);
            }

            if (checkpoint == Aria2RuntimeLifecycle.Aria2PauseCheckpoint.Paused)
            {
                _ = await CancelAndObserveStatusAsync(
                    transferCancellation,
                    statusTask,
                    pauseConfirmed: true).ConfigureAwait(true);
                LogPauseConfirmed("websocket");
                return DownloadTransferResult.Paused();
            }

            var racedStatus = await CancelAndObserveStatusAsync(
                transferCancellation,
                statusTask,
                pauseConfirmed: false).ConfigureAwait(true);
            if (racedStatus != null)
            {
                return await CreateTransferResultAsync(racedStatus, request)
                    .ConfigureAwait(true);
            }

            return await ResolveUnconfirmedPauseAsync(
                ariaManager,
                activeGid,
                request,
                triggerLayer: "websocket").ConfigureAwait(true);
        }
        finally
        {
            await transferCancellation.CancelAsync().ConfigureAwait(true);
            await ObserveRacedTaskCompletionAsync(statusTask).ConfigureAwait(true);
            await ObserveRacedTaskCompletionAsync(pauseRequestTask).ConfigureAwait(true);
            await ObserveRacedTaskCompletionAsync(pauseCheckpointTask).ConfigureAwait(true);
        }
    }

    private async Task<Aria2RuntimeLifecycle.Aria2PauseCheckpoint>
        PauseAndWaitForCheckpointAsync(
            string gid,
            CancellationToken cancellationToken)
    {
        using var waiter = _runtimeLifecycle.RegisterPauseWaiter(gid);
        using var pauseCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        var pauseTask = _ariaClient.PauseAsync(gid, pauseCancellation.Token);
        var completedTask = await Task.WhenAny(pauseTask, waiter.Completion)
            .ConfigureAwait(true);
        if (ReferenceEquals(completedTask, waiter.Completion))
        {
            var checkpoint = await waiter.Completion.ConfigureAwait(true);
            if (checkpoint != Aria2RuntimeLifecycle.Aria2PauseCheckpoint.Disconnected)
            {
                await pauseCancellation.CancelAsync().ConfigureAwait(true);
                await ObservePauseRpcAfterCheckpointAsync(
                    gid,
                    pauseTask,
                    pauseCancellation,
                    checkpoint).ConfigureAwait(true);
                return checkpoint;
            }
        }

        AriaPause pause;
        try
        {
            pause = await pauseTask.ConfigureAwait(true);
        }
        catch (Exception exception) when (
            IsPauseCommunicationFailure(exception)
            && waiter.Completion.IsCompleted)
        {
            var checkpoint = await waiter.Completion.ConfigureAwait(true);
            LogPauseCommunicationFailure(
                "pause-rpc",
                exception,
                checkpoint == Aria2RuntimeLifecycle.Aria2PauseCheckpoint.Paused);
            return checkpoint;
        }

        if (pause is not { Result: { } pausedGid }
            || !string.Equals(pausedGid, gid, StringComparison.Ordinal))
        {
            if (waiter.Completion.IsCompleted)
            {
                var checkpoint = await waiter.Completion.ConfigureAwait(true);
                _logger.LogWarningMessage(
                    "aria2 pause RPC response was rejected after a notification checkpoint; " +
                    $"layer=pause-rpc; pauseConfirmed={IsPaused(checkpoint)}.");
                return checkpoint;
            }

            throw new InvalidOperationException("aria2 rejected the pause request.");
        }

        return await waiter.Completion.WaitAsync(cancellationToken).ConfigureAwait(true);
    }

    private async Task ObservePauseRpcAfterCheckpointAsync(
        string gid,
        Task<AriaPause> pauseTask,
        CancellationTokenSource pauseCancellation,
        Aria2RuntimeLifecycle.Aria2PauseCheckpoint checkpoint)
    {
        try
        {
            var pause = await pauseTask.ConfigureAwait(true);
            if (pause is not { Result: { } pausedGid }
                || !string.Equals(pausedGid, gid, StringComparison.Ordinal))
            {
                _logger.LogWarningMessage(
                    "aria2 pause RPC response was rejected after a notification checkpoint; " +
                    $"layer=pause-rpc; pauseConfirmed={IsPaused(checkpoint)}.");
            }
        }
        catch (OperationCanceledException) when (pauseCancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (IsPauseCommunicationFailure(exception))
        {
            LogPauseCommunicationFailure(
                "pause-rpc",
                exception,
                checkpoint == Aria2RuntimeLifecycle.Aria2PauseCheckpoint.Paused);
        }
    }

    private async Task<DownloadTransferResult> ResolveUnconfirmedPauseAsync(
        AriaManager ariaManager,
        string gid,
        DownloadTransferRequest request,
        string triggerLayer)
    {
        AriaTellStatus status;
        try
        {
            status = await _ariaClient
                .TellStatus(gid, request.CancellationToken)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is HttpRequestException
            or IOException
            or TimeoutException
            or Newtonsoft.Json.JsonException)
        {
            LogPauseCommunicationFailure(
                "status-rpc",
                exception,
                pauseConfirmed: false);
            return DownloadTransferResult.Failed(
                DownloadTransferFailureKind.TransientNetwork,
                "download.transfer.aria2-rpc");
        }

        if (status is not { Result: { } statusResult })
        {
            _logger.LogWarningMessage(
                "aria2 pause remains unconfirmed; " +
                $"trigger={triggerLayer}; layer=status-rpc; state=missing.");
            return DownloadTransferResult.Failed(
                DownloadTransferFailureKind.TransientNetwork,
                "download.transfer.aria2-rpc");
        }

        var state = AriaManager.ClassifyStatus(statusResult);
        if (state == AriaDownloadState.Paused)
        {
            LogPauseConfirmed("status-rpc");
            return DownloadTransferResult.Paused();
        }

        var terminalStatus = await ariaManager.ResolveTerminalStatusAsync(
            gid,
            statusResult,
            lastKnownFilePath: null,
            cancellationToken: request.CancellationToken).ConfigureAwait(true);
        if (terminalStatus != null)
        {
            return await CreateTransferResultAsync(terminalStatus, request)
                .ConfigureAwait(true);
        }

        _logger.LogWarningMessage(
            "aria2 pause remains unconfirmed; " +
            $"trigger={triggerLayer}; layer=status-rpc; state={state}.");
        return DownloadTransferResult.Failed(
            DownloadTransferFailureKind.TransientNetwork,
            "download.transfer.aria2-rpc");
    }

    private async Task<DownloadTransferResult> CreateTransferResultAsync(
        AriaDownloadStatus status,
        DownloadTransferRequest request)
    {
        if (status is { Result: DownloadResult.SUCCESS })
        {
            var finalFile = Path.Combine(request.Directory, request.FileName);
            var integrity = DownloadFileIntegrity.Check(
                finalFile,
                request.ExpectedBytes);
            if (!integrity.IsUsable)
            {
                _logger.LogInformationMessage(
                    integrity.Reason ?? "Downloaded media file is not usable.");
                return DownloadTransferResult.Failed(
                    DownloadTransferFailureKind.InvalidMedia,
                    "download.transfer.invalid-media");
            }

            return DownloadTransferResult.Succeeded();
        }

        if (ShouldClearBackendIdentity(status.ErrorCode))
        {
            await request.SetBackendIdentityAsync(
                null,
                request.CancellationToken).ConfigureAwait(true);
        }

        return Aria2TransferFailureClassifier.Classify(
            status.ErrorCode,
            status.ErrorMessage);
    }

    private async Task<AriaDownloadStatus?> CancelAndObserveStatusAsync(
        CancellationTokenSource cancellation,
        Task<AriaDownloadStatus> statusTask,
        bool pauseConfirmed)
    {
        await cancellation.CancelAsync().ConfigureAwait(true);
        try
        {
            return await statusTask.ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception) when (IsPauseCommunicationFailure(exception))
        {
            LogPauseCommunicationFailure("status-poll", exception, pauseConfirmed);
            return null;
        }
    }

    private void LogPauseConfirmed(string source)
    {
        _logger.LogInformationMessage(
            $"aria2 pause result; result=paused; source={source}.");
    }

    private void LogPauseCommunicationFailure(
        string layer,
        Exception exception,
        bool pauseConfirmed)
    {
        _logger.LogWarningMessage(
            "aria2 pause communication failed; " +
            $"layer={layer}; pauseConfirmed={(pauseConfirmed ? "true" : "false")}; " +
            $"type={exception.GetType().Name}.",
            exception);
    }

    private static bool IsPauseCommunicationFailure(Exception exception)
    {
        return exception is HttpRequestException
            or IOException
            or TimeoutException
            or Newtonsoft.Json.JsonException;
    }

    private static bool IsPaused(
        Aria2RuntimeLifecycle.Aria2PauseCheckpoint checkpoint) =>
        checkpoint == Aria2RuntimeLifecycle.Aria2PauseCheckpoint.Paused;

    private static async Task ObserveRacedTaskCompletionAsync(Task? task)
    {
        if (task != null)
        {
            await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private async Task<AriaTaskPreparation> EnsureAriaTaskAsync(
        DownloadTransferRequest request)
    {
        var resolution = await _addressResolver.ResolveAsync(
            request.Urls[0],
            _networkSettings.UserAgent,
            LoginHelper.GetLoginInfoCookiesString(),
            request.CancellationToken).ConfigureAwait(true);
        if (resolution.ErrorCode != null)
        {
            return AriaTaskPreparation.Rejected(resolution.ErrorCode);
        }

        var resolvedAddress = resolution.Address
            ?? throw new InvalidOperationException("The accepted aria2 address is missing.");
        var taskHeaders = resolution.Headers
            ?? throw new InvalidOperationException("The accepted aria2 task headers are missing.");
        var gid = string.IsNullOrWhiteSpace(request.BackendIdentity)
            ? null
            : request.BackendIdentity;
        string? existingStatus = null;
        if (!string.IsNullOrWhiteSpace(gid))
        {
            var status = await _ariaClient
                .TellStatus(gid, request.CancellationToken)
                .ConfigureAwait(true);
            if (status is not { Result: { } statusResult })
            {
                if (IsNotFound(status.Error))
                {
                    gid = null;
                    await request.SetBackendIdentityAsync(
                        null,
                        request.CancellationToken).ConfigureAwait(true);
                }
                else
                {
                    throw new InvalidOperationException(
                        "aria2 rejected the status request.");
                }
            }
            else
            {
                existingStatus = statusResult.Status;
            }
        }

        if (gid == null)
        {
            var option = new AriaSendOption
            {
                Dir = request.Directory,
                Out = request.FileName,
                Continue = "true",
                AllowOverwrite = "true",
                AutoFileRenaming = "false",
                UserAgent = taskHeaders.UserAgent,
                Headers = taskHeaders.Headers,
                Split = _networkSettings.AriaSplit.ToString(CultureInfo.InvariantCulture),
                MaxConnectionPerServer = _networkSettings.AriaMaxConnectionPerServer
                    .ToString(CultureInfo.InvariantCulture),
                MinSplitSize = $"{_networkSettings.AriaMinSplitSize}M",
                MaxTries = "1",
                RetryWait = "0",
                AlwaysResume = "false",
                MaxResumeFailureTries = "0"
            };
            if (_httpsProxyAddress != null)
            {
                option.HttpsProxy = _httpsProxyAddress.AbsoluteUri;
            }

            var added = await _ariaClient.AddUriAsync(
                [resolvedAddress.AbsoluteUri],
                option).ConfigureAwait(true);
            if (added is not { Result: { } addedGid } ||
                string.IsNullOrWhiteSpace(addedGid))
            {
                throw new InvalidOperationException(
                    "aria2 rejected the addUri request.");
            }

            gid = addedGid;
            await request.SetBackendIdentityAsync(gid, request.CancellationToken).ConfigureAwait(true);
        }
        else if (string.Equals(existingStatus, "paused", StringComparison.Ordinal))
        {
            await RefreshOptionsAndUnpauseAsync(
                _ariaClient,
                gid,
                taskHeaders,
                request.CancellationToken).ConfigureAwait(true);
        }

        return AriaTaskPreparation.Ready(gid);
    }

    internal static async Task RefreshOptionsAndUnpauseAsync(
        AriaClient ariaClient,
        string gid,
        AriaTaskHeaders taskHeaders,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ariaClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(gid);
        ArgumentNullException.ThrowIfNull(taskHeaders);
        cancellationToken.ThrowIfCancellationRequested();

        var options = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["header"] = taskHeaders.Headers.ToArray(),
            ["user-agent"] = taskHeaders.UserAgent
        };
        var changed = await ariaClient
            .ChangeOptionAsync(gid, options)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(true);
        if (changed is not { Result: "OK" })
        {
            throw new InvalidOperationException(
                "aria2 rejected the task credential refresh.");
        }

        var unpaused = await ariaClient
            .UnpauseAsync(gid)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(true);
        if (unpaused is not { Result: { } unpausedGid }
            || string.IsNullOrWhiteSpace(unpausedGid))
        {
            throw new InvalidOperationException(
                "aria2 rejected the unpause request.");
        }
    }

    private static bool IsNotFound(AriaError? error)
    {
        return error?.Message.Contains(
            "is not found",
            StringComparison.OrdinalIgnoreCase) == true;
    }

    private static Uri? ResolveHttpsProxyAddress(NetworkApplicationSettings settings)
    {
        if (settings.IsAriaHttpProxy != AllowStatus.Yes)
        {
            return null;
        }

        if (!AriaHttpsProxyPolicy.TryCreateConnectProxyUri(
                settings.AriaHttpProxy,
                settings.AriaHttpProxyListenPort,
                out var proxyAddress))
        {
            throw new InvalidOperationException(
                "The aria2 HTTPS download proxy must be a local HTTP CONNECT endpoint.");
        }

        return proxyAddress;
    }

    internal static bool ShouldClearBackendIdentity(string? errorCode)
    {
        return !string.IsNullOrWhiteSpace(errorCode) &&
               !errorCode.StartsWith("rpc-", StringComparison.Ordinal);
    }

    private DownloadProgress? CreateProgress(string activeGid, AriaProgressEventArgs eventArgs)
    {
        if (!string.Equals(activeGid, eventArgs.Gid, StringComparison.Ordinal))
        {
            return null;
        }

        var percent = eventArgs.TotalLength == 0
            ? 0
            : (float)eventArgs.CompletedLength / eventArgs.TotalLength * 100;
        _diagnosticLogger.LogSpeed(
            Name,
            eventArgs.Gid,
            eventArgs.CompletedLength,
            eventArgs.TotalLength,
            eventArgs.Speed);
        return new DownloadProgress(
            Math.Clamp(percent, 0, 100),
            eventArgs.CompletedLength,
            Math.Max(eventArgs.CompletedLength, eventArgs.TotalLength),
            eventArgs.Speed,
            $"{Format.FormatFileSize(eventArgs.CompletedLength)}/{Format.FormatFileSize(eventArgs.TotalLength)}",
            Format.FormatSpeedWithBandwidth(eventArgs.Speed));
    }

    public void Dispose()
    {
        try
        {
            _runtimeLifecycle.Dispose();
            ReleaseRuntimeRegistration();
        }
        finally
        {
            _addressResolver.Dispose();
        }
    }

    private void ReleaseRuntimeRegistration()
    {
        Interlocked.Exchange(ref _runtimeRegistration, null)?.Dispose();
    }
}

internal sealed record AriaTaskPreparation(string? Gid, string? ErrorCode)
{
    public static AriaTaskPreparation Ready(string gid) => new(gid, ErrorCode: null);

    public static AriaTaskPreparation Rejected(string errorCode) => new(Gid: null, errorCode);
}
