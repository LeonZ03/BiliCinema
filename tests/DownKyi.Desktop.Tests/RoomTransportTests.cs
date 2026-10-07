using System.Net;
using System.Net.WebSockets;
using DownKyi.RoomServer;
using DownKyi.Services.Watch;
using Microsoft.AspNetCore.Builder;

namespace DownKyi.Desktop.Tests;

public sealed class RoomTransportTests
{
    public static bool LiveTunnelEnabled => Environment.GetEnvironmentVariable("BILICINEMA_LIVE_TUNNEL_TEST") == "1";

    [Fact]
    public async Task GuestsCanJoinAgainAfterHostClosesAndImmediatelyCreatesAnotherRoom()
    {
        var app = CreateServer();
        await using (app.ConfigureAwait(false))
        {
            await app.StartAsync(TestContext.Current.CancellationToken);
            var address = WebSocketAddress(app);
            var host = new WatchRoomClient();
            await using var hostLifetime = host.ConfigureAwait(true);
            var guest = new WatchRoomClient();
            await using var guestLifetime = guest.ConfigureAwait(true);
            await host.ConnectAsync(address, true, null, TestContext.Current.CancellationToken);
            var firstConnection = host.ConnectionCancellation;
            var firstCode = host.RoomCode;
            var joined = await guest.ConnectAsync(address, false, firstCode, TestContext.Current.CancellationToken);
            Assert.Equal(2, joined.MemberCount);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            guest.Closed += () => closed.TrySetResult();
            await host.CloseRoomAsync(TestContext.Current.CancellationToken);
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await host.ConnectAsync(address, true, null, TestContext.Current.CancellationToken);
            Assert.True(firstConnection.IsCancellationRequested);
            Assert.False(host.ConnectionCancellation.IsCancellationRequested);
            Assert.NotEqual(firstCode, host.RoomCode);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    guest.ConnectAsync(address, false, firstCode, TestContext.Current.CancellationToken));
                Assert.Contains("房间已结束", error.Message, StringComparison.Ordinal);
            }
            var rejoined = await guest.ConnectAsync(address, false, host.RoomCode, TestContext.Current.CancellationToken);
            Assert.Equal(2, rejoined.MemberCount);
            Assert.True(host.Connected);
            Assert.True(guest.Connected);
        }
    }

    [Fact]
    public async Task PendingWelcomeDoesNotExposeConnectedRoomToPlaybackMonitor()
    {
        var app = CreateServer();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Use(async (context, next) =>
        {
            if (context.Request.Path != "/ws") { await next(context).ConfigureAwait(false); return; }
            using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            using var first = await WireProtocol.ReceiveAsync(socket, context.RequestAborted).ConfigureAwait(false);
            received.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted).ConfigureAwait(false); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // The test cancels the incomplete welcome handshake below.
            }
        });
        await using (app.ConfigureAwait(false))
        {
            await app.StartAsync(TestContext.Current.CancellationToken);
            var client = new WatchRoomClient();
            await using var clientLifetime = client.ConfigureAwait(true);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var connecting = client.ConnectAsync(WebSocketAddress(app), true, null, cancellation.Token);
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(client.Connected);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting);
            Assert.False(client.Connected);
        }
    }

    [Fact]
    public async Task SlowWebSocketRouteIsAllowedToCompleteReadinessCheck()
    {
        var app = CreateServer();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path == "/ws")
                await Task.Delay(TimeSpan.FromSeconds(5), context.RequestAborted).ConfigureAwait(false);
            await next(context).ConfigureAwait(false);
        });
        await using (app.ConfigureAwait(false))
        {
            await app.StartAsync(TestContext.Current.CancellationToken);
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            await QuickRoomTunnel.ProbePublicRouteAsync(http, new Uri(app.Urls.Single()),
                TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task FailedWebSocketUpgradeReportsGatewayFailure()
    {
        var app = CreateServer();
        var rejectUpgrade = true;
        app.Use(async (context, next) =>
        {
            if (rejectUpgrade && context.Request.Path == "/ws")
            {
                context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                return;
            }
            await next(context).ConfigureAwait(false);
        });
        await using (app.ConfigureAwait(false))
        {
            await app.StartAsync(TestContext.Current.CancellationToken);
            var baseAddress = new Uri(app.Urls.Single());
            using var http = new HttpClient();
            await Assert.ThrowsAsync<WebSocketException>(() =>
                QuickRoomTunnel.ProbePublicRouteAsync(http, baseAddress, TestContext.Current.CancellationToken));
            var client = new WatchRoomClient();
            await using var clientLifetime = client.ConfigureAwait(true);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.ConnectAsync(WebSocketAddress(app), true, null, TestContext.Current.CancellationToken));
            Assert.Contains("HTTP 503", error.Message, StringComparison.Ordinal);
            Assert.False(client.Connected);
            rejectUpgrade = false;
            await QuickRoomTunnel.ProbePublicRouteAsync(http, baseAddress, TestContext.Current.CancellationToken);
            await client.ConnectAsync(WebSocketAddress(app), true, null, TestContext.Current.CancellationToken);
            Assert.True(client.Connected);
        }
    }

    [Fact(SkipUnless = nameof(LiveTunnelEnabled), Skip = "Requires explicit live Cloudflare Tunnel investigation.")]
    public async Task LiveTunnelStartsFreshRoutesAndGuestsJoinBothRooms()
    {
        var app = CreateServer("http://127.0.0.1:5077");
        var websocketRequests = 0;
        var originRequests = 0;
        var lastWebSocketStatus = 0;
        app.Use(async (context, next) =>
        {
            if (context.Request.Path == "/ws")
            {
                Interlocked.Increment(ref websocketRequests);
                if (context.Request.Headers.ContainsKey("Origin")) Interlocked.Increment(ref originRequests);
            }
            await next(context).ConfigureAwait(false);
            if (context.Request.Path == "/ws") lastWebSocketStatus = context.Response.StatusCode;
        });
        await using (app.ConfigureAwait(false))
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(5));
            await app.StartAsync(deadline.Token);
            var tunnel = new QuickRoomTunnel();
            await using var tunnelLifetime = tunnel.ConfigureAwait(true);
            var host = new WatchRoomClient();
            await using var hostLifetime = host.ConfigureAwait(true);
            var guest = new WatchRoomClient();
            await using var guestLifetime = guest.ConfigureAwait(true);
            string firstAddress;
            var progress = new CreationProgressRecorder();
            try { firstAddress = await tunnel.StartAsync(progress, deadline.Token); }
            catch (InvalidOperationException error)
            {
                throw new InvalidOperationException($"{error.Message} 本机收到 WebSocket 请求 {websocketRequests} 次，Origin 请求 {originRequests} 次，"
                    + $"WebSocket 最后状态 {lastWebSocketStatus}。", error);
            }
            Assert.Contains(progress.Values, value => value.Percentage == 40);
            Assert.Contains(progress.Values, value => value.Percentage == 70);
            Assert.Equal(85, progress.Values[^1].Percentage);
            await host.ConnectAsync(WebSocketAddress(app), true, null, deadline.Token);
            var first = await guest.ConnectAsync(firstAddress, false, host.RoomCode, deadline.Token);
            Assert.Equal(2, first.MemberCount);
            await host.CloseRoomAsync(deadline.Token);
            await guest.LeaveAsync();
            await host.LeaveAsync();
            await tunnel.StopAsync();
            Assert.False(tunnel.IsRunning);
            Assert.Null(tunnel.ServiceAddress);
            var secondAddress = await tunnel.StartAsync(deadline.Token);
            Assert.False(string.Equals(firstAddress, secondAddress, StringComparison.Ordinal),
                "Recreation reused the old public route.");
            await host.ConnectAsync(WebSocketAddress(app), true, null, deadline.Token);
            var second = await guest.ConnectAsync(secondAddress, false, host.RoomCode, deadline.Token);
            Assert.Equal(2, second.MemberCount);
        }
    }

    private static WebApplication CreateServer(string address = "http://127.0.0.1:0") =>
        RoomServerHost.Build(["--RoomServer:ListenUrl", address, "--Logging:LogLevel:Default", "None"]);

    private static string WebSocketAddress(WebApplication app) => new UriBuilder(app.Urls.Single())
    {
        Scheme = "ws",
        Path = "/ws"
    }.Uri.AbsoluteUri;

    private sealed class CreationProgressRecorder : IProgress<RoomCreationProgress>
    {
        public System.Collections.Generic.List<RoomCreationProgress> Values { get; } = [];
        public void Report(RoomCreationProgress value) => Values.Add(value);
    }
}
