using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Runtime.Versioning;
using System.Text.Json;
using DownKyi.ProcessSupervision;
using DownKyi.Services.Watch;

namespace DownKyi.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed class BilibiliPlayerSurfaceTests
{
    [Fact]
    public async Task ChatKeepsTypingFocusAndDefersPlayerProbesUntilDismissed()
    {
        await RunFixtureAsync("chat-focus", new Dictionary<string, string>
        {
            ["/* PLAYER_SCRIPT */"] = BilibiliWebPlaybackSession.FocusPlayerScript,
            ["/* CHAT_OVERLAY */"] = RoomChatOverlayScript.Build([], null, null, fullscreen: true,
                inRoom: true, memberCount: 2, revision: 0, toastSequence: 0,
                toastRemainingMilliseconds: 0, hostActionNotices: [], hostActionToastSequence: 0),
            ["/* WINDOWED_OVERLAY */"] = RoomChatOverlayScript.Build([], null, null, fullscreen: false,
                inRoom: true, memberCount: 2, revision: 0, toastSequence: 0,
                toastRemainingMilliseconds: 0, hostActionNotices: [], hostActionToastSequence: 0)
        });
    }

    [Fact]
    public async Task MiniModeAndReplacedPlayerRecoverRealControlsWithoutChangingPlayback()
    {
        await RunFixtureAsync("player-surface", new Dictionary<string, string>
        {
            ["/* PLAYER_SCRIPT */"] = BilibiliWebPlaybackSession.FocusPlayerScript
        });
    }

    [Fact]
    public async Task HostActionsRenderOnExistingOverlayAndExpireSeparately()
    {
        static string Overlay(bool fullscreen, int sequence, params WatchRoomActionNotice[] notices) =>
            RoomChatOverlayScript.Build([], null, null, fullscreen, inRoom: true, memberCount: 5,
                revision: 0, toastSequence: 0, toastRemainingMilliseconds: 0,
                hostActionNotices: notices, hostActionToastSequence: sequence);

        await RunFixtureAsync("room-overlay", new Dictionary<string, string>
        {
            ["/* PLAYER_SCRIPT */"] = BilibiliWebPlaybackSession.FocusPlayerScript,
            ["/* EMPTY_OVERLAY */"] = Overlay(false, 0),
            ["/* PAUSE_OVERLAY */"] = Overlay(false, 1, new WatchRoomActionNotice(1, "房主暂停了播放", 1000)),
            ["/* SEEK_OVERLAY */"] = Overlay(false, 2, new(1, "房主暂停了播放", 750),
                new(2, "房主跳转到 00:20:00", 1000)),
            ["/* FULLSCREEN_OVERLAY */"] = Overlay(true, 5, new(3, "房主开始播放", 1000),
                new(4, "房主调整为 1.5 倍速", 1000), new(5, "房主切换了视频", 1000))
        });
    }

    private static async Task RunFixtureAsync(string fixtureName, IReadOnlyDictionary<string, string> scripts)
    {
        var edge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft", "Edge", "Application", "msedge.exe");
        Assert.True(File.Exists(edge), "This browser layout regression requires Microsoft Edge.");
        var directory = Path.Combine(Path.GetTempPath(), $"bilicinema-player-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var fixture = typeof(BilibiliPlayerSurfaceTests).Assembly.GetManifestResourceStream(
                $"DownKyi.Windows.Tests.Fixtures.{fixtureName}.html");
            Assert.NotNull(fixture);
            using var reader = new StreamReader(fixture);
            var html = await reader.ReadToEndAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
            foreach (var (marker, script) in scripts)
                html = html.Replace(marker, script, StringComparison.Ordinal);
            var path = Path.Combine(directory, "player.html");
            await File.WriteAllTextAsync(path, html, TestContext.Current.CancellationToken).ConfigureAwait(false);
            var start = new ProcessStartInfo(edge)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[]
                     {
                         "--headless", "--disable-gpu", "--no-first-run", "--no-default-browser-check",
                         "--disable-background-networking", "--disable-component-update",
                         "--host-resolver-rules=MAP * ~NOTFOUND", "--remote-debugging-port=0",
                         $"--user-data-dir={Path.Combine(directory, "profile")}", new Uri(path).AbsoluteUri
                     })
            {
                start.ArgumentList.Add(argument);
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            using var scope = await OwnedProcessScope.StartAsync(start, deadline.Token).ConfigureAwait(false);
            var output = scope.Host.StandardOutput.ReadToEndAsync(deadline.Token);
            var errors = scope.Host.StandardError.ReadToEndAsync(deadline.Token);
            try
            {
                // Use real compositor frames. dump-dom's virtual clock can expire
                // all JS timers before a pending requestAnimationFrame is rendered.
                var debugger = await FindPageDebuggerAsync(directory, deadline.Token).ConfigureAwait(false);
                using var socket = new ClientWebSocket();
                await socket.ConnectAsync(debugger, deadline.Token).ConfigureAwait(false);
                var sequence = 0;
                try
                {
                    while (true)
                    {
                        var result = await ReadFixtureResultAsync(socket, ++sequence, deadline.Token).ConfigureAwait(false);
                        if (result.StartsWith("INPUT:", StringComparison.Ordinal))
                        {
                            // Browser-dispatched input is trusted and follows real
                            // focus/hit testing, unlike dispatchEvent-only fixtures.
                            using var input = JsonDocument.Parse(result[6..]);
                            var method = input.RootElement.GetProperty("method").GetString()!;
                            Assert.StartsWith("Input.", method, StringComparison.Ordinal);
                            await CallDebuggerAsync(socket, ++sequence, method,
                                input.RootElement.GetProperty("params"), deadline.Token).ConfigureAwait(false);
                            await CallDebuggerAsync(socket, ++sequence, "Runtime.evaluate",
                                new { expression = "window.completeInput()" }, deadline.Token).ConfigureAwait(false);
                            continue;
                        }
                        if (result != "RUNNING" && result.Length > 0)
                        {
                            Assert.Equal("PASS", result);
                            break;
                        }
                        await Task.Delay(100, deadline.Token).ConfigureAwait(false);
                    }
                }
                finally
                {
                    // Let Chromium close its profile databases before removing the
                    // directory. The Job still owns forced cleanup on failure.
                    if (socket.State == WebSocketState.Open)
                    {
                        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        var close = JsonSerializer.SerializeToUtf8Bytes(new { id = ++sequence, method = "Browser.close" });
                        await socket.SendAsync(close.AsMemory(), WebSocketMessageType.Text, true, shutdown.Token)
                            .ConfigureAwait(false);
                        await scope.Host.WaitForExitAsync(shutdown.Token).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                try
                {
                    await scope.TerminateAsync(new CleanupDeadline(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
                }
                finally
                {
                    await Task.WhenAll(output, errors).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<Uri> FindPageDebuggerAsync(string directory, CancellationToken cancellationToken)
    {
        var portFile = Path.Combine(directory, "profile", "DevToolsActivePort");
        var port = 0;
        while (port == 0)
        {
            if (File.Exists(portFile))
            {
                try
                {
                    // Chromium creates this file before its write handle closes.
                    // Readiness requires a complete port, not just file existence.
                    using var stream = new FileStream(portFile, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
                    using var reader = new StreamReader(stream);
                    var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (int.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out var candidate)
                        && candidate is > 0 and <= 65535) port = candidate;
                }
                catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
                {
                    // A startup sharing/lock violation is still pending readiness;
                    // the caller's deadline bounds this wait and preserves evidence.
                }
            }
            if (port == 0) await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        using var handler = new HttpClientHandler { UseProxy = false, CheckCertificateRevocationList = true };
        using var client = new HttpClient(handler);
        var endpoint = new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/json/list");
        while (true)
        {
            using var response = await client.GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var target in document.RootElement.EnumerateArray())
            {
                if (target.GetProperty("type").GetString() == "page")
                    return new Uri(target.GetProperty("webSocketDebuggerUrl").GetString()!);
            }
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadFixtureResultAsync(ClientWebSocket socket, int sequence,
        CancellationToken cancellationToken)
    {
        var response = await CallDebuggerAsync(socket, sequence, "Runtime.evaluate",
            new { expression = "document.querySelector('#result')?.textContent || ''", returnByValue = true },
            cancellationToken).ConfigureAwait(false);
        return response.GetProperty("result").GetProperty("value").GetString() ?? "";
    }

    private static async Task<JsonElement> CallDebuggerAsync(ClientWebSocket socket, int sequence,
        string method, object parameters, CancellationToken cancellationToken)
    {
        var request = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = sequence,
            method,
            @params = parameters
        });
        await socket.SendAsync(request.AsMemory(), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        var buffer = new byte[4096];
        while (true)
        {
            using var message = new MemoryStream();
            ValueWebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close)
                    throw new InvalidOperationException("Browser debugger closed before fixture completion.");
                await message.WriteAsync(buffer.AsMemory(0, received.Count), cancellationToken).ConfigureAwait(false);
            } while (!received.EndOfMessage);
            using var document = JsonDocument.Parse(message.ToArray());
            if (document.RootElement.TryGetProperty("id", out var id) && id.GetInt32() == sequence)
            {
                Assert.False(document.RootElement.TryGetProperty("error", out var error), error.ToString());
                return document.RootElement.GetProperty("result").Clone();
            }
        }
    }
}
