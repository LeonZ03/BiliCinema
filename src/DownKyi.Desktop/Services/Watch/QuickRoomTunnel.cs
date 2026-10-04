using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DownKyi.Services.Watch;

internal sealed class QuickRoomTunnel : IDisposable
{
    private static readonly Uri LocalHealthAddress = new("http://127.0.0.1:5077/health");
    private static readonly Regex PublicAddressPattern = new(
        @"https://[a-z0-9-]+\.trycloudflare\.com\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private Process? _process;

    public string? ServiceAddress { get; private set; }
    public bool IsRunning => _process is { HasExited: false };

    public async Task<string> StartAsync(CancellationToken cancellationToken)
    {
        if (IsRunning && ServiceAddress is { } existing)
        {
            return existing;
        }

        Stop();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        try
        {
            using var response = await client.GetAsync(LocalHealthAddress, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception error) when (error is HttpRequestException
            || error is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "本机房间服务未响应。请检查 5077 端口是否被其他程序占用，再重试创建房间。", error);
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
            RedirectStandardError = true
        };
        start.ArgumentList.Add("tunnel");
        start.ArgumentList.Add("--no-autoupdate");
        start.ArgumentList.Add("--url");
        start.ArgumentList.Add("http://127.0.0.1:5077");

        var published = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
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
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("无法启动 Cloudflare Tunnel。");
            }

            _process = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            string publicAddress;
            try
            {
                publicAddress = await published.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
                await WaitForPublicHealthAsync(client, publicAddress, process, deadline.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("Cloudflare 地址建立超时。请检查网络后重试。");
            }

            if (process.HasExited)
            {
                throw new InvalidOperationException("Cloudflare Tunnel 已退出。请检查网络后重试。");
            }

            ServiceAddress = "wss" + publicAddress[5..] + "/ws";
            return ServiceAddress;
        }
        catch (Win32Exception error)
        {
            Stop();
            process.Dispose();
            throw new InvalidOperationException(
                "找不到 cloudflared。请先安装 Cloudflare Tunnel 客户端，再创建房间。", error);
        }
        catch
        {
            Stop();
            process.Dispose();
            throw;
        }
    }

    private static async Task WaitForPublicHealthAsync(
        HttpClient client, string publicAddress, Process process, CancellationToken cancellationToken)
    {
        var healthAddress = new Uri(publicAddress + "/health");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new InvalidOperationException("Cloudflare Tunnel 已退出。请检查网络后重试。");
            }

            try
            {
                using var response = await client.GetAsync(healthAddress, cancellationToken)
                    .ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // DNS and the public route can take a few seconds to become available.
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A single probe timed out; the overall startup deadline still applies.
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    private void Stop()
    {
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

    public void Dispose() => Stop();
}
