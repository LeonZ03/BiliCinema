using System.Net;
using System.Net.WebSockets;
using System.Text.Json;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DownKyi.Desktop")]

namespace DownKyi.RoomServer;

// Shared by the standalone server and the desktop application. The listener remains loopback-only.
internal static class RoomServerHost
{
    public static WebApplication Build(string[]? args = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args ?? []);
        builder.WebHost.UseUrls(builder.Configuration["RoomServer:ListenUrl"] ?? "http://127.0.0.1:5077");
        builder.Services.AddSingleton(_ => new RoomCoordinator());
        builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<RoomCoordinator>());

        WebApplication app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.Map("/ws", async (HttpContext context, RoomCoordinator rooms) =>
        {
            IPAddress? remote = context.Connection.RemoteIpAddress;
            if (!context.Request.IsHttps && (remote is null || !IPAddress.IsLoopback(remote)))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            if (context.Request.Headers.ContainsKey("Origin"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            ClientConnection connection = new(socket);
            Task? sender = null;
            try
            {
                using JsonDocument first = await WireProtocol.ReceiveAsync(socket, context.RequestAborted).ConfigureAwait(false);
                rooms.Attach(connection, first.RootElement);
                sender = connection.SendLoopAsync(context.RequestAborted);

                while (socket.State == WebSocketState.Open)
                {
                    try
                    {
                        using JsonDocument message = await WireProtocol.ReceiveAsync(socket, context.RequestAborted).ConfigureAwait(false);
                        rooms.Handle(connection, message.RootElement);
                    }
                    catch (RoomProtocolException exception)
                    {
                        if (exception.Code is "connection_closed" or "message_too_large" or "text_required")
                        {
                            break;
                        }

                        connection.Enqueue(WireProtocol.Error(exception.Code));
                    }
                }
            }
            catch (RoomProtocolException exception)
            {
                if (sender is null && socket.State == WebSocketState.Open)
                {
                    await WireProtocol.SendDirectErrorAsync(socket, exception.Code, context.RequestAborted).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
            }
            catch (WebSocketException)
            {
            }
            finally
            {
                RoomCoordinator.Detach(connection);
                connection.Complete();
                socket.Abort();
                if (sender is not null)
                {
                    try
                    {
                        await sender.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (WebSocketException)
                    {
                    }
                }
            }
        });
        return app;
    }
}
