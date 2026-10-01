using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading.Channels;
using DownKyi.Application.Diagnostics;
using DownKyi.Core.Aria2cNet.Client;
using DownKyi.Core.Aria2cNet.Server;
using DownKyi.Domain.Downloads;
using DownKyi.Services.Download;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DownKyi.Tests;

public sealed class Aria2PauseEventTests
{
    [Fact]
    public async Task NotificationListenerRoutesFourEventsByExactGid()
    {
        using var settings = new TestSettingsStore();
        using var socket = new TestAria2NotificationSocket();
        var client = CreateCapabilityClient();
        using var lifecycle = new Aria2RuntimeLifecycle(
            settings.Store.Current.Network,
            client,
            new DownloadDiagnosticLogger(NullLogger<DownloadDiagnosticLogger>.Instance),
            new AriaServer(NullLoggerFactory.Instance),
            NullLogger<Aria2RuntimeLifecycle>.Instance,
            ownsAriaServer: true,
            localEndpoint: new LocalAriaRpcEndpoint(6800, "test-token"),
            notificationSocketFactory: () => socket);

        await InvokeNotificationLifecycleAsync(
            lifecycle,
            "StartNotificationListenerAsync",
            TestContext.Current.CancellationToken);
        try
        {
            using var wrongGid = lifecycle.RegisterPauseWaiter("wrong-gid");
            using var pause = lifecycle.RegisterPauseWaiter("pause-gid");
            using var complete = lifecycle.RegisterPauseWaiter("complete-gid");
            using var error = lifecycle.RegisterPauseWaiter("error-gid");
            using var removed = lifecycle.RegisterPauseWaiter("removed-gid");

            socket.Emit("aria2.onDownloadPause", "wrong-gid");
            Assert.Equal(
                Aria2RuntimeLifecycle.Aria2PauseCheckpoint.Paused,
                await wrongGid.Completion.WaitAsync(TestContext.Current.CancellationToken));
            Assert.False(pause.Completion.IsCompleted);

            socket.Emit("aria2.onDownloadPause", "pause-gid");
            socket.Emit("aria2.onDownloadComplete", "complete-gid");
            socket.Emit("aria2.onDownloadError", "error-gid");
            socket.Emit("aria2.onDownloadStop", "removed-gid");

            Assert.Equal(
                Aria2RuntimeLifecycle.Aria2PauseCheckpoint.Paused,
                await pause.Completion.WaitAsync(TestContext.Current.CancellationToken));
            Assert.Equal(
                Aria2RuntimeLifecycle.Aria2PauseCheckpoint.Complete,
                await complete.Completion.WaitAsync(TestContext.Current.CancellationToken));
            Assert.Equal(
                Aria2RuntimeLifecycle.Aria2PauseCheckpoint.Error,
                await error.Completion.WaitAsync(TestContext.Current.CancellationToken));
            Assert.Equal(
                Aria2RuntimeLifecycle.Aria2PauseCheckpoint.Removed,
                await removed.Completion.WaitAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            await InvokeNotificationLifecycleAsync(
                lifecycle,
                "StopNotificationListenerAsync",
                CancellationToken.None);
        }
    }

    [Fact]
    public async Task DisconnectAtomicallyRejectsWaiterRegisteredWhilePreviousWaitersComplete()
    {
        using var settings = new TestSettingsStore();
        using var socket = new TestAria2NotificationSocket();
        using var lifecycle = new Aria2RuntimeLifecycle(
            settings.Store.Current.Network,
            CreateCapabilityClient(),
            new DownloadDiagnosticLogger(NullLogger<DownloadDiagnosticLogger>.Instance),
            new AriaServer(NullLoggerFactory.Instance),
            NullLogger<Aria2RuntimeLifecycle>.Instance,
            ownsAriaServer: true,
            localEndpoint: new LocalAriaRpcEndpoint(6800, "test-token"),
            notificationSocketFactory: () => socket);

        await InvokeNotificationLifecycleAsync(
            lifecycle,
            "StartNotificationListenerAsync",
            TestContext.Current.CancellationToken);
        using var existing = lifecycle.RegisterPauseWaiter("existing-gid");
        using var completionScheduler = new BlockingTaskScheduler();
        var scheduledCompletion = existing.Completion.ContinueWith(
            static _ => { },
            CancellationToken.None,
            TaskContinuationOptions.None,
            completionScheduler);
        try
        {
            socket.Disconnect();
            await completionScheduler.QueueEntered.WaitAsync(
                TestContext.Current.CancellationToken);

            using var racing = lifecycle.RegisterPauseWaiter("racing-gid");

            Assert.True(racing.Completion.IsCompleted);
            Assert.Equal(
                Aria2RuntimeLifecycle.Aria2PauseCheckpoint.Disconnected,
                await racing.Completion.ConfigureAwait(true));
        }
        finally
        {
            completionScheduler.Release();
            await scheduledCompletion.WaitAsync(TestContext.Current.CancellationToken);
            await InvokeNotificationLifecycleAsync(
                lifecycle,
                "StopNotificationListenerAsync",
                CancellationToken.None);
        }

        Assert.Equal(
            Aria2RuntimeLifecycle.Aria2PauseCheckpoint.Disconnected,
            await existing.Completion.ConfigureAwait(true));
    }

    [Fact]
    public async Task PauseWaiterIsRegisteredBeforeRpcAndDoesNotPollForCheckpoint()
    {
        const string gid = "pause-event-gid";
        using var socket = new TestAria2NotificationSocket();
        var pauseRequested = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var initialStatusStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var statusCancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var tellStatusCount = 0;
        var client = CreateClient(async (_, payload, cancellationToken) =>
        {
            var method = JObject.Parse(payload)["method"]?.Value<string>();
            switch (method)
            {
                case "aria2.getVersion":
                    return CreateVersionResponse();
                case "aria2.addUri":
                    return CreateResponse(gid);
                case "aria2.tellStatus":
                    Interlocked.Increment(ref tellStatusCount);
                    initialStatusStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        statusCancellationObserved.TrySetResult();
                        throw;
                    }

                    throw new InvalidOperationException("The status request was not canceled.");
                case "aria2.pause":
                    var receiveAttempt = socket.ReceiveAttemptCount;
                    socket.Emit("aria2.onDownloadPause", gid);
                    await socket.WaitForReceiveAttemptAsync(
                        receiveAttempt + 1,
                        cancellationToken).ConfigureAwait(false);
                    return CreateResponse(gid);
                default:
                    throw new InvalidOperationException($"Unexpected RPC method '{method}'.");
            }
        });

        var result = await RunTransferAsync(
            client,
            socket,
            gid,
            pauseRequested,
            initialStatusStarted,
            beforeAwait: () =>
            {
                pauseRequested.TrySetResult();
                return Task.CompletedTask;
            });

        Assert.Equal(DownloadTransferOutcome.Paused, result.Outcome);
        Assert.Equal(1, Volatile.Read(ref tellStatusCount));
        await statusCancellationObserved.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PauseCheckpointWinsWhenPauseRpcResponseIsLost()
    {
        const string gid = "pause-rpc-lost-gid";
        using var socket = new TestAria2NotificationSocket();
        var pauseRequested = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var initialStatusStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var statusCancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new RecordingLogger<Aria2TransferBackend>();
        var client = CreateClient(async (_, payload, cancellationToken) =>
        {
            var method = JObject.Parse(payload)["method"]?.Value<string>();
            switch (method)
            {
                case "aria2.getVersion":
                    return CreateVersionResponse();
                case "aria2.addUri":
                    return CreateResponse(gid);
                case "aria2.tellStatus":
                    initialStatusStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        statusCancellationObserved.TrySetResult();
                        throw;
                    }

                    throw new InvalidOperationException("The status request was not canceled.");
                case "aria2.pause":
                    var receiveAttempt = socket.ReceiveAttemptCount;
                    socket.Emit("aria2.onDownloadPause", gid);
                    await socket.WaitForReceiveAttemptAsync(
                        receiveAttempt + 1,
                        CancellationToken.None).ConfigureAwait(false);
                    throw new HttpRequestException("The pause response was lost.");
                default:
                    throw new InvalidOperationException($"Unexpected RPC method '{method}'.");
            }
        });

        var result = await RunTransferAsync(
            client,
            socket,
            gid,
            pauseRequested,
            initialStatusStarted,
            beforeAwait: () =>
            {
                pauseRequested.TrySetResult();
                return Task.CompletedTask;
            },
            backendLogger: logger);

        Assert.Equal(DownloadTransferOutcome.Paused, result.Outcome);
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Warning
            && entry.Message.Contains(
                "layer=pause-rpc; pauseConfirmed=true; type=HttpRequestException",
                StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Information
            && entry.Message.Contains(
                "result=paused; source=websocket",
                StringComparison.Ordinal));
        await statusCancellationObserved.Task
            .WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    [Fact]
    public async Task PauseCheckpointWinsWhenCanceledStatusPollFaults()
    {
        const string gid = "status-poll-fault-gid";
        using var socket = new TestAria2NotificationSocket();
        var pauseRequested = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var initialStatusStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var statusCancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new RecordingLogger<Aria2TransferBackend>();
        var client = CreateClient(async (_, payload, cancellationToken) =>
        {
            var method = JObject.Parse(payload)["method"]?.Value<string>();
            switch (method)
            {
                case "aria2.getVersion":
                    return CreateVersionResponse();
                case "aria2.addUri":
                    return CreateResponse(gid);
                case "aria2.tellStatus":
                    initialStatusStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        statusCancellationObserved.TrySetResult();
                        throw new HttpRequestException(
                            "The status transport failed while cancellation was observed.");
                    }

                    throw new InvalidOperationException("The status request was not canceled.");
                case "aria2.pause":
                    var receiveAttempt = socket.ReceiveAttemptCount;
                    socket.Emit("aria2.onDownloadPause", gid);
                    await socket.WaitForReceiveAttemptAsync(
                        receiveAttempt + 1,
                        cancellationToken).ConfigureAwait(false);
                    return CreateResponse(gid);
                default:
                    throw new InvalidOperationException($"Unexpected RPC method '{method}'.");
            }
        });

        var result = await RunTransferAsync(
            client,
            socket,
            gid,
            pauseRequested,
            initialStatusStarted,
            beforeAwait: () =>
            {
                pauseRequested.TrySetResult();
                return Task.CompletedTask;
            },
            backendLogger: logger);

        Assert.Equal(DownloadTransferOutcome.Paused, result.Outcome);
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Warning
            && entry.Message.Contains(
                "layer=status-poll; pauseConfirmed=true; type=HttpRequestException",
                StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Information
            && entry.Message.Contains(
                "result=paused; source=websocket",
                StringComparison.Ordinal));
        await statusCancellationObserved.Task
            .WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    [Fact]
    public async Task CustomAriaPauseUsesRpcAcknowledgementWithoutNotificationSocket()
    {
        const string gid = "custom-pause-gid";
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-custom-aria-pause-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var pauseRequested = 0;
        var notificationSocketFactoryCalls = 0;
        var tellStatusCalls = 0;
        var pauseRpcCalls = 0;
        var initialStatusStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInitialStatus = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        try
        {
            using var settings = new TestSettingsStore();
            var client = CreateClient(async (_, payload, cancellationToken) =>
            {
                var method = JObject.Parse(payload)["method"]?.Value<string>();
                switch (method)
                {
                    case "aria2.getVersion":
                        return CreateVersionResponse();
                    case "aria2.addUri":
                        return CreateResponse(gid);
                    case "aria2.tellStatus":
                        Interlocked.Increment(ref tellStatusCalls);
                        initialStatusStarted.TrySetResult();
                        await releaseInitialStatus.Task
                            .WaitAsync(cancellationToken)
                            .ConfigureAwait(false);
                        return CreateStatusResponse("active", outputPath: null);
                    case "aria2.pause":
                        Interlocked.Increment(ref pauseRpcCalls);
                        return CreateResponse(gid);
                    default:
                        throw new InvalidOperationException($"Unexpected RPC method '{method}'.");
                }
            });
            using var probeHandler = new AcceptingHttpMessageHandler();
            using var replacementResolver = AriaDownloadAddressResolver.CreateForTest(
                probeHandler);
            using var backend = new Aria2TransferBackend(
                settings.Store.Current.Network,
                client,
                new AriaRuntimeClientRegistry(),
                new DownloadDiagnosticLogger(NullLogger<DownloadDiagnosticLogger>.Instance),
                new AriaServer(NullLoggerFactory.Instance),
                NullLoggerFactory.Instance,
                NullLogger<Aria2TransferBackend>.Instance,
                ownsAriaServer: false,
                localEndpoint: null,
                notificationSocketFactory: () =>
                {
                    Interlocked.Increment(ref notificationSocketFactoryCalls);
                    throw new InvalidOperationException(
                        "Custom aria2 must not create a notification socket.");
                });
            ReplaceAddressResolverForTest(backend, replacementResolver);
            await backend.StartAsync(requestCancellation.Token).ConfigureAwait(true);
            try
            {
                var request = new DownloadTransferRequest(
                    new DownloadTaskId("custom-aria-pause"),
                    BackendIdentity: null,
                    Urls: ["https://download.example/media"],
                    Directory: directory,
                    FileName: "media.tmp",
                    ExpectedBytes: 0,
                    EnsureActive: static () => { },
                    IsPauseRequested: () => Volatile.Read(ref pauseRequested) == 1,
                    WaitForPauseRequestedAsync: static _ => Task.FromException(
                        new InvalidOperationException(
                            "Custom aria2 must not wait for a notification-backed pause signal.")),
                    PublishProgress: static _ => { },
                    PersistProgressAsync: static (_, _) => Task.CompletedTask,
                    SetBackendIdentityAsync: static (_, _) => Task.CompletedTask,
                    CancellationToken: requestCancellation.Token,
                    StagingDirectory: directory);
                var transferTask = backend.TransferAsync(request);
                await initialStatusStarted.Task
                    .WaitAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true);
                Interlocked.Exchange(ref pauseRequested, 1);
                releaseInitialStatus.TrySetResult();

                var result = await transferTask
                    .WaitAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true);

                Assert.Equal(DownloadTransferOutcome.Paused, result.Outcome);
                Assert.Equal(0, Volatile.Read(ref notificationSocketFactoryCalls));
                Assert.Equal(1, Volatile.Read(ref tellStatusCalls));
                Assert.Equal(1, Volatile.Read(ref pauseRpcCalls));
            }
            finally
            {
                await backend.StopAsync(CancellationToken.None).ConfigureAwait(true);
            }
        }
        finally
        {
            await requestCancellation.CancelAsync().ConfigureAwait(true);
            releaseInitialStatus.TrySetResult();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("aria2.onDownloadComplete", "complete", "Succeeded")]
    [InlineData("aria2.onDownloadError", "error", "Failed")]
    [InlineData("aria2.onDownloadStop", "removed", "Failed")]
    public async Task TerminalEventRetainsExistingTransferSemantics(
        string notificationMethod,
        string terminalStatus,
        string expectedOutcome)
    {
        const string gid = "terminal-event-gid";
        using var socket = new TestAria2NotificationSocket();
        var pauseRequested = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var initialStatusStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStatus = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pauseRpcCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        string? outputPath = null;
        var client = CreateClient(async (_, payload, cancellationToken) =>
        {
            var method = JObject.Parse(payload)["method"]?.Value<string>();
            switch (method)
            {
                case "aria2.getVersion":
                    return CreateVersionResponse();
                case "aria2.addUri":
                    return CreateResponse(gid);
                case "aria2.tellStatus":
                    initialStatusStarted.TrySetResult();
                    await releaseStatus.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    return CreateStatusResponse(terminalStatus, outputPath);
                case "aria2.pause":
                    var receiveAttempt = socket.ReceiveAttemptCount;
                    socket.Emit(notificationMethod, gid);
                    await socket.WaitForReceiveAttemptAsync(
                        receiveAttempt + 1,
                        TestContext.Current.CancellationToken).ConfigureAwait(false);
                    pauseRpcCompleted.TrySetResult();
                    return CreateResponse(gid);
                case "aria2.removeDownloadResult":
                    return CreateResponse(gid);
                default:
                    throw new InvalidOperationException($"Unexpected RPC method '{method}'.");
            }
        });

        var result = await RunTransferAsync(
            client,
            socket,
            gid,
            pauseRequested,
            initialStatusStarted,
            beforeAwait: async () =>
            {
                pauseRequested.TrySetResult();
                await pauseRpcCompleted.Task
                    .WaitAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true);
                releaseStatus.TrySetResult();
            },
            outputPathObserver: path => outputPath = path);

        Assert.Equal(expectedOutcome, result.Outcome.ToString());
    }

    [Theory]
    [InlineData("paused", "Paused")]
    [InlineData("complete", "Succeeded")]
    [InlineData("error", "Failed")]
    [InlineData("removed", "Failed")]
    [InlineData("active", "Failed")]
    [InlineData("unknown", "Failed")]
    [InlineData("query-failure", "Failed")]
    public async Task DisconnectChecksStatusOnceAndNeverAssumesPaused(
        string reconciledStatus,
        string expectedOutcome)
    {
        const string gid = "disconnect-gid";
        using var socket = new TestAria2NotificationSocket();
        var pauseRequested = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var initialStatusStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var initialStatusCancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var tellStatusCount = 0;
        string? outputPath = null;
        var client = CreateClient(async (_, payload, cancellationToken) =>
        {
            var method = JObject.Parse(payload)["method"]?.Value<string>();
            switch (method)
            {
                case "aria2.getVersion":
                    return CreateVersionResponse();
                case "aria2.addUri":
                    return CreateResponse(gid);
                case "aria2.pause":
                    socket.Disconnect();
                    return CreateResponse(gid);
                case "aria2.tellStatus" when Interlocked.Increment(ref tellStatusCount) == 1:
                    initialStatusStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        initialStatusCancellationObserved.TrySetResult();
                        throw;
                    }

                    throw new InvalidOperationException("The status request was not canceled.");
                case "aria2.tellStatus" when reconciledStatus == "query-failure":
                    throw new HttpRequestException("Reconciliation failed.");
                case "aria2.tellStatus":
                    return CreateStatusResponse(reconciledStatus, outputPath);
                case "aria2.removeDownloadResult":
                    return CreateResponse(gid);
                default:
                    throw new InvalidOperationException($"Unexpected RPC method '{method}'.");
            }
        });

        var result = await RunTransferAsync(
            client,
            socket,
            gid,
            pauseRequested,
            initialStatusStarted,
            beforeAwait: () =>
            {
                pauseRequested.TrySetResult();
                return Task.CompletedTask;
            },
            outputPathObserver: path => outputPath = path);

        Assert.Equal(expectedOutcome, result.Outcome.ToString());
        Assert.Equal(2, Volatile.Read(ref tellStatusCount));
        await initialStatusCancellationObserved.Task
            .WaitAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("pause")]
    public async Task FailedRacedOperationCancelsAndObservesOtherWork(string failedOperation)
    {
        const string gid = "race-failure-gid";
        using var socket = new TestAria2NotificationSocket();
        var pauseRequested = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var otherCancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var initialStatusStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var tellStatusCount = 0;
        var client = CreateClient(async (_, payload, cancellationToken) =>
        {
            var method = JObject.Parse(payload)["method"]?.Value<string>();
            switch (method)
            {
                case "aria2.getVersion":
                    return CreateVersionResponse();
                case "aria2.addUri":
                    return CreateResponse(gid);
                case "aria2.tellStatus" when failedOperation == "status":
                    initialStatusStarted.TrySetResult();
                    throw new HttpRequestException("Status failed.");
                case "aria2.tellStatus" when Interlocked.Increment(ref tellStatusCount) == 1:
                    initialStatusStarted.TrySetResult();
                    await WaitUntilCanceledAsync(otherCancellationObserved, cancellationToken)
                        .ConfigureAwait(false);
                    throw new InvalidOperationException("The status request was not canceled.");
                case "aria2.tellStatus":
                    return CreateStatusResponse("active", outputPath: null);
                case "aria2.pause":
                    throw new HttpRequestException("Pause failed.");
                default:
                    throw new InvalidOperationException($"Unexpected RPC method '{method}'.");
            }
        });

        var result = await RunTransferAsync(
            client,
            socket,
            gid,
            pauseRequested,
            initialStatusStarted,
            beforeAwait: () =>
            {
                if (failedOperation == "pause")
                {
                    pauseRequested.TrySetResult();
                }

                return Task.CompletedTask;
            },
            waitForPauseAsync: failedOperation == "status"
                ? token => WaitUntilCanceledAsync(otherCancellationObserved, token)
                : null);

        Assert.Equal(DownloadTransferOutcome.Failed, result.Outcome);
        await otherCancellationObserved.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<DownloadTransferResult> RunTransferAsync(
        AriaClient client,
        TestAria2NotificationSocket socket,
        string gid,
        TaskCompletionSource pauseRequested,
        TaskCompletionSource initialStatusStarted,
        Func<Task> beforeAwait,
        Action<string>? outputPathObserver = null,
        Func<CancellationToken, Task>? waitForPauseAsync = null,
        ILogger<Aria2TransferBackend>? backendLogger = null)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-aria-pause-event-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var outputPath = Path.Combine(directory, "media.tmp");
        await File.WriteAllBytesAsync(
            outputPath,
            [1],
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        outputPathObserver?.Invoke(outputPath);
        using var settings = new TestSettingsStore();
        using var probeHandler = new AcceptingHttpMessageHandler();
        using var replacementResolver = AriaDownloadAddressResolver.CreateForTest(
            probeHandler);
        using var backend = new Aria2TransferBackend(
            settings.Store.Current.Network,
            client,
            new AriaRuntimeClientRegistry(),
            new DownloadDiagnosticLogger(NullLogger<DownloadDiagnosticLogger>.Instance),
            new AriaServer(NullLoggerFactory.Instance),
            NullLoggerFactory.Instance,
            backendLogger ?? NullLogger<Aria2TransferBackend>.Instance,
            ownsAriaServer: true,
            localEndpoint: new LocalAriaRpcEndpoint(6800, "test-token"),
            notificationSocketFactory: () => socket);
        ReplaceAddressResolverForTest(backend, replacementResolver);
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var runtimeLifecycle = GetRuntimeLifecycleForTest(backend);
        await InvokeNotificationLifecycleAsync(
            runtimeLifecycle,
            "StartNotificationListenerAsync",
            requestCancellation.Token).ConfigureAwait(true);
        try
        {
            var request = new DownloadTransferRequest(
                new DownloadTaskId("aria-pause-event"),
                BackendIdentity: null,
                Urls: ["https://download.example/media"],
                Directory: directory,
                FileName: Path.GetFileName(outputPath),
                ExpectedBytes: 1,
                EnsureActive: static () => { },
                IsPauseRequested: () => pauseRequested.Task.IsCompleted,
                WaitForPauseRequestedAsync: waitForPauseAsync
                    ?? (token => pauseRequested.Task.WaitAsync(token)),
                PublishProgress: static _ => { },
                PersistProgressAsync: static (_, _) => Task.CompletedTask,
                SetBackendIdentityAsync: static (_, _) => Task.CompletedTask,
                CancellationToken: requestCancellation.Token,
                StagingDirectory: directory);
            var transferTask = backend.TransferAsync(request);
            await initialStatusStarted.Task
                .WaitAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            await beforeAwait().ConfigureAwait(true);
            return await transferTask
                .WaitAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
        }
        finally
        {
            await requestCancellation.CancelAsync().ConfigureAwait(true);
            pauseRequested.TrySetResult();
            await InvokeNotificationLifecycleAsync(
                runtimeLifecycle,
                "StopNotificationListenerAsync",
                CancellationToken.None).ConfigureAwait(true);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static AriaClient CreateCapabilityClient()
    {
        return CreateClient((_, payload, _) =>
        {
            var method = JObject.Parse(payload)["method"]?.Value<string>();
            return Task.FromResult<string?>(method == "aria2.getVersion"
                ? CreateVersionResponse()
                : throw new InvalidOperationException($"Unexpected RPC method '{method}'."));
        });
    }

    private static AriaClient CreateClient(
        Func<Uri, string, CancellationToken, Task<string?>> requestAsync)
    {
        return new AriaClient(
            "http://localhost",
            6800,
            "test-token",
            requestAsync);
    }

    private static string CreateVersionResponse()
    {
        return CreateResponse(new
        {
            version = "1.37.0",
            enabledFeatures = new[] { Aria2RuntimeLifecycle.SecureRedirectFeature }
        });
    }

    private static string CreateStatusResponse(string status, string? outputPath)
    {
        return CreateResponse(new
        {
            status,
            errorCode = status == "error" ? "1" : "0",
            errorMessage = status == "error" ? "terminal test failure" : string.Empty,
            totalLength = "1",
            completedLength = status == "complete" ? "1" : "0",
            downloadSpeed = "0",
            files = outputPath == null
                ? Array.Empty<object>()
                : new object[] { new { path = outputPath } }
        });
    }

    private static string CreateResponse(object result)
    {
        return JsonConvert.SerializeObject(new
        {
            jsonrpc = "2.0",
            id = "pause-event-test",
            result
        });
    }

    private static async Task WaitUntilCanceledAsync(
        TaskCompletionSource cancellationObserved,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancellationObserved.TrySetResult();
            throw;
        }
    }

    private static void ReplaceAddressResolverForTest(
        Aria2TransferBackend backend,
        AriaDownloadAddressResolver replacement)
    {
        var field = typeof(Aria2TransferBackend).GetField(
            "_addressResolver",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The aria2 address resolver field is missing.");
        var original = Assert.IsType<AriaDownloadAddressResolver>(field.GetValue(backend));
        field.SetValue(backend, replacement);
        original.Dispose();
    }

    private static Aria2RuntimeLifecycle GetRuntimeLifecycleForTest(
        Aria2TransferBackend backend)
    {
        var field = typeof(Aria2TransferBackend).GetField(
            "_runtimeLifecycle",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The aria2 runtime lifecycle field is missing.");
        return Assert.IsType<Aria2RuntimeLifecycle>(field.GetValue(backend));
    }

    private static async Task InvokeNotificationLifecycleAsync(
        Aria2RuntimeLifecycle lifecycle,
        string methodName,
        CancellationToken cancellationToken)
    {
        var method = typeof(Aria2RuntimeLifecycle).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"The aria2 notification lifecycle method '{methodName}' is missing.");
        var task = Assert.IsAssignableFrom<Task>(method.Invoke(
            lifecycle,
            [cancellationToken]));
        await task.ConfigureAwait(true);
    }

    private sealed class AcceptingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                RequestMessage = request,
                Content = new ByteArrayContent([0])
            });
        }
    }

    private sealed class BlockingTaskScheduler : TaskScheduler, IDisposable
    {
        private readonly TaskCompletionSource _queueEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _release = new(initialState: false);

        public Task QueueEntered => _queueEntered.Task;

        public void Release() => _release.Set();

        protected override IEnumerable<Task>? GetScheduledTasks() => null;

        protected override void QueueTask(Task task)
        {
            _queueEntered.TrySetResult();
            _release.Wait();
            TryExecuteTask(task);
        }

        protected override bool TryExecuteTaskInline(
            Task task,
            bool taskWasPreviouslyQueued) => false;

        public void Dispose() => _release.Dispose();
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            _entries.Enqueue(new LogEntry(logLevel, formatter(state, exception)));
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message);
}

internal sealed class TestAria2NotificationSocket : IAria2NotificationSocket
{
    private readonly Channel<NotificationFrame> _frames = Channel.CreateUnbounded<NotificationFrame>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    private readonly object _gate = new();
    private readonly List<ReceiveAttemptWaiter> _receiveAttemptWaiters = [];
    private int _receiveAttemptCount;
    private int _state = (int)WebSocketState.None;

    public WebSocketState State => (WebSocketState)Volatile.Read(ref _state);

    public int ReceiveAttemptCount => Volatile.Read(ref _receiveAttemptCount);

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Exchange(ref _state, (int)WebSocketState.Open);
        return Task.CompletedTask;
    }

    public async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        SignalReceiveAttempt();
        var frame = await _frames.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (frame.MessageType == WebSocketMessageType.Close)
        {
            Interlocked.Exchange(ref _state, (int)WebSocketState.CloseReceived);
            return new ValueWebSocketReceiveResult(
                0,
                WebSocketMessageType.Close,
                endOfMessage: true);
        }

        frame.Payload.AsMemory().CopyTo(buffer);
        return new ValueWebSocketReceiveResult(
            frame.Payload.Length,
            frame.MessageType,
            endOfMessage: true);
    }

    public Task CloseOutputAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Exchange(ref _state, (int)WebSocketState.CloseSent);
        return Task.CompletedTask;
    }

    public void Emit(string method, string gid)
    {
        var payload = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
        {
            jsonrpc = "2.0",
            method,
            @params = new[] { new { gid } }
        }));
        Assert.True(_frames.Writer.TryWrite(
            new NotificationFrame(WebSocketMessageType.Text, payload)));
    }

    public void Disconnect()
    {
        Assert.True(_frames.Writer.TryWrite(
            new NotificationFrame(WebSocketMessageType.Close, [])));
    }

    public Task WaitForReceiveAttemptAsync(
        int expectedCount,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_receiveAttemptCount >= expectedCount)
            {
                return Task.CompletedTask;
            }

            var waiter = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _receiveAttemptWaiters.Add(new ReceiveAttemptWaiter(expectedCount, waiter));
            return waiter.Task.WaitAsync(cancellationToken);
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _state, (int)WebSocketState.Closed);
        _frames.Writer.TryComplete();
    }

    private void SignalReceiveAttempt()
    {
        List<TaskCompletionSource> completed = [];
        lock (_gate)
        {
            var count = ++_receiveAttemptCount;
            for (var index = _receiveAttemptWaiters.Count - 1; index >= 0; index--)
            {
                var waiter = _receiveAttemptWaiters[index];
                if (count >= waiter.ExpectedCount)
                {
                    completed.Add(waiter.Completion);
                    _receiveAttemptWaiters.RemoveAt(index);
                }
            }
        }

        foreach (var completion in completed)
        {
            completion.TrySetResult();
        }
    }

    private sealed record NotificationFrame(
        WebSocketMessageType MessageType,
        byte[] Payload);

    private sealed record ReceiveAttemptWaiter(
        int ExpectedCount,
        TaskCompletionSource Completion);
}
