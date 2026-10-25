using System.Net;
using System.Net.WebSockets;
using DownKyi.Services.Download;
using DownKyi.TestInfrastructure;
using Microsoft.AspNetCore.Builder;

namespace DownKyi.Desktop.Tests;

public sealed class Aria2NotificationSocketTests
{
    [Fact]
    public async Task LoopbackNotificationSocketBypassesConfiguredRejectingProxy()
    {
        var proxy = new LoopbackHttpServer(_ =>
            new LoopbackResponse(HttpStatusCode.BadGateway, "proxy rejected request"));
        await using var proxyLifetime = proxy.ConfigureAwait(false);
        var app = WebApplication.CreateBuilder(["--urls", "http://127.0.0.1:0"]).Build();
        app.UseWebSockets();
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Use(async (context, next) =>
        {
            if (context.Request.Path != "/jsonrpc")
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            accepted.TrySetResult();
            try
            {
                var buffer = new byte[1024];
                var received = await socket.ReceiveAsync(buffer, context.RequestAborted).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "test complete",
                        context.RequestAborted).ConfigureAwait(false);
                    closeCompleted.TrySetResult();
                }
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                return;
            }
        });
        await using var appLifetime = app.ConfigureAwait(false);
        await app.StartAsync(TestContext.Current.CancellationToken);
        var uri = new UriBuilder(app.Urls.Single())
        {
            Scheme = "ws",
            Path = "/jsonrpc"
        }.Uri;
        using var socketClient = new ClientWebSocketAria2NotificationSocket(new WebProxy(proxy.Url));

        await socketClient.ConnectAsync(uri, TestContext.Current.CancellationToken);
        await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(WebSocketState.Open, socketClient.State);
        Assert.Equal(0, proxy.RequestCount);
        await socketClient.CloseOutputAsync(
            WebSocketCloseStatus.NormalClosure,
            "test complete",
            TestContext.Current.CancellationToken);
        await closeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }
}
