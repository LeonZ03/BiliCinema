using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DownKyi.Services.Watch;

internal sealed record RoomCreationProgress(int Percentage, string Step);

internal sealed class QuickRoomTunnel : IAsyncDisposable
{
    private static readonly Uri LocalHealthAddress = new("http://127.0.0.1:5077/health");
    private static readonly Regex PublicAddressPattern = new(
        @"https://[a-z0-9-]+\.trycloudflare\.com\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private Process? _process;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    public string? ServiceAddress { get; private set; }
    public bool IsRunning => _process is { HasExited: false };

    public Task<string> StartAsync(CancellationToken cancellationToken) => StartAsync(null, cancellationToken);

    public async Task<string> StartAsync(IProgress<RoomCreationProgress>? progress,
        CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await StartCoreAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        finally { _lifecycleGate.Release(); }
    }

    private async Task<string> StartCoreAsync(IProgress<RoomCreationProgress>? progress,
        CancellationToken cancellationToken)
    {
        // Each room creation owns a fresh route, even if the previous process is alive.
        await StopCoreAsync().ConfigureAwait(false);
        progress?.Report(new(20, "检查本机房间服务"));
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            using var localDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            localDeadline.CancelAfter(TimeSpan.FromSeconds(4));
            using var response = await client.GetAsync(LocalHealthAddress, localDeadline.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception error) when (error is HttpRequestException
            || error is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "本机房间服务未响应。请检查 5077 端口是否被其他程序占用，再重试创建房间。", error);
        }

        progress?.Report(new(30, "准备房间连接工具"));
        var adjacentExecutable = Path.Combine(AppContext.BaseDirectory, "cloudflared.exe");
        var bundledExecutable = await Task.Run(BundledTools.EnsureTunnelTool, cancellationToken)
            .ConfigureAwait(false);
        var start = new ProcessStartInfo(bundledExecutable ?? (File.Exists(adjacentExecutable)
            ? adjacentExecutable : "cloudflared.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("tunnel");
        start.ArgumentList.Add("--no-autoupdate");
        // The current Windows network commonly blocks QUIC/UDP 7844. Explicit
        // HTTP/2 avoids cloudflared spending time probing QUIC before fallback.
        start.ArgumentList.Add("--protocol");
        start.ArgumentList.Add("http2");
        start.ArgumentList.Add("--url");
        start.ArgumentList.Add("http://127.0.0.1:5077");

        var published = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _process = new Process { StartInfo = start, EnableRaisingEvents = true };
            var process = _process;
            void ReadAddress(object sender, DataReceivedEventArgs args)
            {
                if (args.Data is { } line)
                {
                    var match = PublicAddressPattern.Match(line);
                    if (match.Success)
                    {
                        published.TrySetResult(match.Value);
                    }
                }
            }

            process.OutputDataReceived += ReadAddress;
            process.ErrorDataReceived += ReadAddress;
            process.Exited += (_, _) => published.TrySetException(new InvalidOperationException(
                "Cloudflare Tunnel 已退出。请检查网络，或更新 cloudflared 后重试。"));
            if (!process.Start())
            {
                throw new InvalidOperationException("无法启动 Cloudflare Tunnel。");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            progress?.Report(new(40, "向 Cloudflare 请求新地址"));
            string publicAddress;
            var phase = "生成公开地址";
            try
            {
                using var addressDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                addressDeadline.CancelAfter(TimeSpan.FromSeconds(45));
                publicAddress = await published.Task.WaitAsync(addressDeadline.Token).ConfigureAwait(false);
                progress?.Report(new(55, "地址已生成，等待公网通道就绪"));
                using var routeDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                routeDeadline.CancelAfter(TimeSpan.FromSeconds(90));
                await WaitForPublicHealthAsync(client, publicAddress, process, value =>
                    {
                        phase = value.Step;
                        progress?.Report(value);
                    }, routeDeadline.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException($"Cloudflare {phase}超时，请检查网络后重试。");
            }

            if (process.HasExited)
            {
                throw new InvalidOperationException("Cloudflare Tunnel 已退出。请检查网络后重试。");
            }

            ServiceAddress = "wss" + publicAddress[5..] + "/ws";
            progress?.Report(new(85, "公网通道已就绪"));
            return ServiceAddress;
        }
        catch (Win32Exception error)
        {
            await StopCoreAsync().ConfigureAwait(false);
            throw new InvalidOperationException(
                "找不到 cloudflared。请先安装 Cloudflare Tunnel 客户端，再创建房间。", error);
        }
        catch
        {
            await StopCoreAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task WaitForPublicHealthAsync(
        HttpClient client, string publicAddress, Process process, Action<RoomCreationProgress> reportPhase,
        CancellationToken cancellationToken)
    {
        var attempt = 0;
        var retryDelay = TimeSpan.FromSeconds(1);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new InvalidOperationException("Cloudflare Tunnel 已退出。请检查网络后重试。");
            }

            try
            {
                attempt++;
                await ProbePublicRouteAsync(client, new Uri(publicAddress),
                        value => reportPhase(value with { Step = $"{value.Step} · 第 {attempt} 次检测" }),
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (WebSocketException)
            {
                // The edge route may still be propagating; retry with backoff.
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A single probe timed out; the overall startup deadline still applies.
            }

            await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            retryDelay = TimeSpan.FromSeconds(Math.Min(6, retryDelay.TotalSeconds + 1));
        }
    }

    internal static Task ProbePublicRouteAsync(
        HttpClient client, Uri publicAddress, CancellationToken cancellationToken) =>
        ProbePublicRouteAsync(client, publicAddress, null, cancellationToken);

    private static async Task ProbePublicRouteAsync(
        HttpClient client, Uri publicAddress, Action<RoomCreationProgress>? reportPhase,
        CancellationToken cancellationToken)
    {
        _ = client;
        reportPhase?.Invoke(new(70, "验证公网 WebSocket 连接"));
        using var socketDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        socketDeadline.CancelAfter(TimeSpan.FromSeconds(12));
        using var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        var address = new UriBuilder(publicAddress)
        {
            Scheme = publicAddress.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Path = "/ws"
        }.Uri;
        try { await socket.ConnectAsync(address, socketDeadline.Token).ConfigureAwait(false); }
        catch (WebSocketException)
        {
            if (socket.HttpStatusCode != 0)
                reportPhase?.Invoke(new(70, $"公网 WebSocket 连接（HTTP {(int)socket.HttpStatusCode}），正在重试"));
            throw;
        }
        // No room or account is created by this transport readiness check.
        socket.Abort();
    }

    public async Task StopAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _lifecycleGate.Release(); }
    }

    private async Task StopCoreAsync()
    {
        ServiceAddress = null;
        var process = _process;
        _process = null;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                throw new TimeoutException("Cloudflare Tunnel 未在关闭后及时退出。");
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the state check and the kill request.
        }
        finally
        {
            process.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync().ConfigureAwait(false); }
        finally { _lifecycleGate.Dispose(); }
    }
}
