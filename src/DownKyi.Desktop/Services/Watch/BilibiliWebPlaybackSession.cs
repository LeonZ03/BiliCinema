using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using DownKyi.Core.BiliApi.Login;
using DownKyi.Presentation;

namespace DownKyi.Services.Watch;

// The official embedded player performs its own media requests, quality selection and buffering.
// We only read and control the HTML video element for the existing room protocol.
internal sealed class BilibiliWebPlaybackSession : IDisposable
{
    private const string Video = "document.querySelector('video')";
    private readonly NativeWebView _browser;
    private readonly Uri _playerUrl;
    private bool _disposed;
    private bool _failed;

    private BilibiliWebPlaybackSession(NativeWebView browser, Uri playerUrl)
    {
        _browser = browser;
        _playerUrl = playerUrl;
        _browser.NavigationCompleted += OnNavigationCompleted;
        _browser.AdapterDestroyed += OnAdapterDestroyed;
    }

    public bool HasExited => _disposed || _failed;

    public static async Task<BilibiliWebPlaybackSession> StartAsync(
        NativeWebView browser, VideoPage page, bool startPaused, CancellationToken cancellationToken)
    {
        if (page.EpisodeId <= 0)
        {
            throw new InvalidOperationException("网页播放器只支持 B 站影片剧集链接。");
        }

        // The view is hosted in a private WebView2 profile. Copy only Bilibili cookies
        // from this application's QR login before the first request to the player.
        browser.Navigate(new Uri("about:blank"));
        NativeWebViewCookieManager? cookieManager = null;
        for (var attempt = 0; attempt < 100 && cookieManager == null; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            cookieManager = browser.TryGetCookieManager();
            if (cookieManager == null)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(true);
            }
        }

        if (cookieManager == null)
        {
            throw new InvalidOperationException("网页播放器未能启动。请确认 WebView2 Runtime 已安装。");
        }

        var cookies = LoginHelper.GetLoginInfoCookies()
            .Where(cookie => !string.IsNullOrWhiteSpace(cookie.Name)
                && !string.IsNullOrWhiteSpace(cookie.Value)
                && IsBilibiliDomain(cookie.Domain))
            .ToArray();
        if (cookies.Length == 0)
        {
            throw new InvalidOperationException("本机没有可用的 B 站登录信息，请重新扫码。");
        }

        foreach (var cookie in cookies)
        {
            cookieManager.AddOrUpdateCookie(new Cookie(cookie.Name, cookie.Value,
                "/", cookie.Domain!) { Secure = true });
        }

        var playerUrl = new Uri($"https://player.bilibili.com/player.html?episodeId={page.EpisodeId.ToString(CultureInfo.InvariantCulture)}&autoplay=1&danmaku=0&muted={(startPaused ? 1 : 0)}");
        var session = new BilibiliWebPlaybackSession(browser, playerUrl);
        try
        {
            browser.Navigate(playerUrl);
            for (var attempt = 0; attempt < 600; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (session._failed)
                {
                    throw new InvalidOperationException("B 站网页播放器加载失败，请检查网络或账号权限。");
                }

                try
                {
                    var ready = await session.InvokeAsync($"{Video}?.readyState ?? 0", cancellationToken)
                        .ConfigureAwait(true);
                    if (ParseNumber(ready) >= 2)
                    {
                        await session.SetPausedAsync(startPaused, cancellationToken).ConfigureAwait(true);
                        if (startPaused)
                        {
                            await session.InvokeAsync($"if ({Video}) {Video}.muted = false",
                                cancellationToken).ConfigureAwait(true);
                        }
                        return session;
                    }
                    if (attempt % 20 == 0)
                    {
                        await session.InvokeAsync($"{Video}?.play().catch(() => {{}})",
                            cancellationToken).ConfigureAwait(true);
                    }
                }
                catch (InvalidOperationException) when (!session._failed)
                {
                    // Navigation can temporarily replace the JavaScript context.
                }

                await Task.Delay(100, cancellationToken).ConfigureAwait(true);
            }

            throw new InvalidOperationException("B 站网页播放器未出现视频，请确认该影片允许网页播放。");
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public async Task<double> GetPositionAsync(CancellationToken cancellationToken)
        => ParseNumber(await InvokeAsync($"{Video}?.currentTime ?? 0", cancellationToken)
            .ConfigureAwait(true));

    public async Task<double> GetDurationAsync(CancellationToken cancellationToken)
        => ParseNumber(await InvokeAsync($"{Video}?.duration ?? 0", cancellationToken)
            .ConfigureAwait(true));

    public async Task<bool> GetBufferingAsync(CancellationToken cancellationToken)
    {
        var result = await InvokeAsync($"!!({Video} && !{Video}.paused && ({Video}.seeking || {Video}.readyState < 3))",
            cancellationToken).ConfigureAwait(true);
        return string.Equals(result.Trim('"'), "true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<(int Width, int Height)?> GetDecodedDimensionsAsync(CancellationToken cancellationToken)
    {
        var width = (int)ParseNumber(await InvokeAsync($"{Video}?.videoWidth ?? 0", cancellationToken)
            .ConfigureAwait(true));
        var height = (int)ParseNumber(await InvokeAsync($"{Video}?.videoHeight ?? 0", cancellationToken)
            .ConfigureAwait(true));
        return width > 0 && height > 0 ? (width, height) : null;
    }

    public async Task SeekAsync(double seconds, CancellationToken cancellationToken)
    {
        if (double.IsFinite(seconds) && seconds >= 0)
        {
            await InvokeAsync($"if ({Video}) {Video}.currentTime = {seconds.ToString("R", CultureInfo.InvariantCulture)}",
                cancellationToken).ConfigureAwait(true);
        }
    }

    public async Task SetPausedAsync(bool paused, CancellationToken cancellationToken)
    {
        await InvokeAsync(paused
                ? $"{Video}?.pause()"
                : $"{Video}?.play().catch(() => {{}})", cancellationToken)
            .ConfigureAwait(true);
    }

    public async Task SetSpeedAsync(double speed, CancellationToken cancellationToken)
    {
        if (double.IsFinite(speed) && speed is >= 0.5 and <= 2)
        {
            await InvokeAsync($"if ({Video}) {Video}.playbackRate = {speed.ToString("R", CultureInfo.InvariantCulture)}",
                cancellationToken).ConfigureAwait(true);
        }
    }

    public static async Task ClearBilibiliCookiesAsync(NativeWebView? browser)
    {
        var manager = browser?.TryGetCookieManager();
        if (manager == null)
        {
            return;
        }

        foreach (var cookie in await manager.GetCookiesAsync().ConfigureAwait(true))
        {
            if (IsBilibiliDomain(cookie.Domain))
            {
                manager.DeleteCookie(cookie);
            }
        }
    }

    private async Task<string> InvokeAsync(string script, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed || _failed)
        {
            throw new InvalidOperationException("网页播放器已经关闭。");
        }

        return await _browser.InvokeScript(script).ConfigureAwait(true) ?? string.Empty;
    }

    private static bool IsBilibiliDomain(string? domain)
        => domain != null && (domain.TrimStart('.').Equals("bilibili.com", StringComparison.OrdinalIgnoreCase)
            || domain.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase));

    private static double ParseNumber(string result)
        => double.TryParse(result.Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture,
            out var number) && double.IsFinite(number) ? number : 0;

    private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs args)
    {
        if (!args.IsSuccess && args.Request == _playerUrl)
        {
            _failed = true;
        }
    }

    private void OnAdapterDestroyed(object? sender, WebViewAdapterEventArgs args) => _failed = true;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _browser.NavigationCompleted -= OnNavigationCompleted;
        _browser.AdapterDestroyed -= OnAdapterDestroyed;
        if (Dispatcher.UIThread.CheckAccess())
        {
            NavigateToBlank();
        }
        else
        {
            Dispatcher.UIThread.Post(NavigateToBlank);
        }
    }

    private void NavigateToBlank()
    {
        try
        {
            _browser.Navigate(new Uri("about:blank"));
        }
        catch (Exception error) when (error is InvalidOperationException
            or ObjectDisposedException or System.Runtime.InteropServices.COMException)
        {
            // Closing the window can destroy WebView2 before this callback runs.
        }
    }
}
