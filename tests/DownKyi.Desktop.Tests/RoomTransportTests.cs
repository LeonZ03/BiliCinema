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
    public async Task GatewayRouteKeyProtectsEmbeddedRoomServer()
    {
        const string routeKey = "test-route-key-for-bilicinema-room-server";
        var app = RoomServerHost.Build([
            "--RoomServer:ListenUrl", "http://127.0.0.1:0",
            "--RoomServer:GatewayRouteKey", routeKey,
            "--Logging:LogLevel:Default", "None"]);
        await using (app.ConfigureAwait(false))
        {
            await app.StartAsync(TestContext.Current.CancellationToken);
            using var http = new HttpClient();
            using var rejected = await http.GetAsync(new Uri(new Uri(app.Urls.Single()), "/health"),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                new Uri(new Uri(app.Urls.Single()), "/health"));
            request.Headers.TryAddWithoutValidation("X-BiliCinema-Route-Key", routeKey);
            using var accepted = await http.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

            var client = new WatchRoomClient();
            await using var clientLifetime = client.ConfigureAwait(true);
            var snapshot = await client.ConnectAsync(WebSocketAddress(app), true, null,
                TestContext.Current.CancellationToken, routeKey);
            Assert.Equal(1, snapshot.MemberCount);
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
            await RoomGatewayTunnel.ProbePublicRouteAsync(new Uri(app.Urls.Single()),
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
            var probeError = await Assert.ThrowsAsync<HttpRequestException>(() =>
                RoomGatewayTunnel.ProbePublicRouteAsync(baseAddress, TestContext.Current.CancellationToken));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, probeError.StatusCode);
            var client = new WatchRoomClient();
            await using var clientLifetime = client.ConfigureAwait(true);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.ConnectAsync(WebSocketAddress(app), true, null, TestContext.Current.CancellationToken));
            Assert.Contains("HTTP 503", error.Message, StringComparison.Ordinal);
            Assert.False(client.Connected);
            rejectUpgrade = false;
            await RoomGatewayTunnel.ProbePublicRouteAsync(baseAddress, TestContext.Current.CancellationToken);
            await client.ConnectAsync(WebSocketAddress(app), true, null, TestContext.Current.CancellationToken);
            Assert.True(client.Connected);
        }
    }

    [Fact(SkipUnless = nameof(LiveTunnelEnabled), Skip = "Requires explicit live Cloudflare Tunnel investigation.")]
    public async Task LiveFixedGatewayRoutesGuestsToRecreatedRooms()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        var tunnel = new RoomGatewayTunnel();
        await using var tunnelLifetime = tunnel.ConfigureAwait(true);
        string? previousRoom = null;
        for (var cycle = 0; cycle < 2; cycle++)
        {
            var app = RoomServerHost.Build([
                "--RoomServer:ListenUrl", "http://127.0.0.1:5077",
                "--RoomServer:GatewayRouteKey", tunnel.LocalRouteKey,
                "--Logging:LogLevel:Default", "None"]);
            await using var appLifetime = app.ConfigureAwait(true);
            await app.StartAsync(deadline.Token);
            var host = new WatchRoomClient();
            await using var hostLifetime = host.ConfigureAwait(true);
            var guest = new WatchRoomClient();
            await using var guestLifetime = guest.ConfigureAwait(true);
            var progress = new CreationProgressRecorder();
            await host.ConnectAsync(WebSocketAddress(app), true, null, deadline.Token, tunnel.LocalRouteKey);
            Assert.NotEqual(previousRoom, host.RoomCode);
            string address;
            try
            {
                address = await tunnel.StartAsync(host.RoomCode!, progress, deadline.Token);
            }
            catch (TimeoutException error)
            {
                HttpStatusCode status;
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
                using (var request = new HttpRequestMessage(HttpMethod.Get, $"https://{tunnel.OriginHost}/health"))
                {
                    request.Headers.TryAddWithoutValidation("X-BiliCinema-Route-Key", tunnel.LocalRouteKey);
                    using var response = await http.SendAsync(request, deadline.Token);
                    status = response.StatusCode;
                }
                throw new InvalidOperationException($"Direct origin HTTP {(int)status}. {error.Message}", error);
            }
            Assert.Contains(progress.Values, value => value.Percentage == 70);
            Assert.Equal(85, progress.Values[^1].Percentage);
            Assert.Equal(RoomGatewayTunnel.ServiceAddress, address);
            Assert.Contains(host.RoomCode!, tunnel.InviteAddress, StringComparison.Ordinal);
            var joined = await guest.ConnectAsync(tunnel.InviteAddress!, false, host.RoomCode, deadline.Token);
            Assert.Equal(2, joined.MemberCount);
            previousRoom = host.RoomCode;
            await host.CloseRoomAsync(deadline.Token);
            await guest.LeaveAsync();
            await host.LeaveAsync();
            await tunnel.StopAsync();
            Assert.False(tunnel.IsRunning);
            Assert.Null(tunnel.InviteAddress);
            var ended = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                guest.ConnectAsync(address, false, previousRoom, deadline.Token));
            Assert.Contains("房间已结束", ended.Message, StringComparison.Ordinal);
            await app.StopAsync(deadline.Token);
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
