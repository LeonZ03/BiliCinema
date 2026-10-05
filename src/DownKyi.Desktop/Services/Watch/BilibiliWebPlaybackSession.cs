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

// Use Bilibili's normal bangumi page rather than its restricted external embed player.
// Its HTML video still provides the existing room protocol's position and playback controls.
internal sealed class BilibiliWebPlaybackSession : IDisposable
{
    private const string Video = "document.querySelector('#bilibili-player video, #bilibiliPlayer video, .bpx-player-container video, video')";
    private const string FocusPlayerScript = """
        (() => {
            const video = document.querySelector('#bilibili-player video, #bilibiliPlayer video, .bpx-player-container video, video');
            if (!video) {
                if (document.body) document.body.style.visibility = 'hidden';
                document.documentElement.style.background = '#000';
                return false;
            }
            let player = [...document.querySelectorAll('#bilibili-player, #bilibiliPlayer, .bpx-player-container, .bilibili-player')]
                .find(element => element.contains(video))
                || video.closest('.player-container, .player-wrap');
            if (!player) {
                for (let parent = video.parentElement; parent && parent !== document.body; parent = parent.parentElement) {
                    const box = parent.getBoundingClientRect();
                    if (box.width >= 320 && box.height >= 180
                        && box.width < window.innerWidth * 2 && box.height < window.innerHeight * 2) player = parent;
                }
            }
            if (!player) return false;
            window.__biliCinemaPlayerRoot = player;
            if (!video.__biliCinemaBound) {
                video.__biliCinemaBound = true;
                for (const type of ['play', 'pause', 'seeked', 'ratechange']) {
                    video.addEventListener(type, () => {
                        const ignored = window.__biliCinemaRemoteEvents?.[type];
                        if (ignored && performance.now() < ignored.until
                            && (type !== 'seeked' || Math.abs(video.currentTime - ignored.position) < 0.4)) {
                            delete window.__biliCinemaRemoteEvents[type];
                            return;
                        }
                        if (typeof invokeCSharpAction === 'function') {
                            invokeCSharpAction(JSON.stringify({ source: 'biliCinemaPlayer',
                                type, position: video.currentTime, rate: video.playbackRate }));
                        }
                    });
                }
            }
            if (!window.__biliCinemaPageActionGuard) {
                window.__biliCinemaPageActionGuard = true;
                for (const type of ['click', 'pointerdown', 'submit']) {
                    document.addEventListener(type, event => {
                        const root = window.__biliCinemaPlayerRoot;
                        if (root && !root.contains(event.target)) {
                            event.preventDefault();
                            event.stopImmediatePropagation();
                        }
                    }, true);
                }
            }
            document.documentElement.style.background = '#000';
            document.documentElement.style.overflow = 'hidden';
            document.body.style.background = '#000';
            document.body.style.overflow = 'hidden';
            document.body.style.margin = '0';
            document.body.style.visibility = 'visible';
            for (let kept = player; kept && kept !== document.body; kept = kept.parentElement) {
                const parent = kept.parentElement;
                if (!parent) break;
                for (const sibling of parent.children) {
                    if (sibling !== kept) {
                        sibling.style.setProperty('display', 'none', 'important');
                        sibling.style.setProperty('pointer-events', 'none', 'important');
                    }
                }
                parent.style.setProperty('overflow', 'visible', 'important');
                parent.style.setProperty('transform', 'none', 'important');
            }
            if (!window.__biliCinemaFocusObserver) {
                window.__biliCinemaFocusObserver = new MutationObserver(() => {
                    if (window.__biliCinemaFocusQueued) return;
                    window.__biliCinemaFocusQueued = true;
                    requestAnimationFrame(() => {
                        window.__biliCinemaFocusQueued = false;
                        const root = window.__biliCinemaPlayerRoot;
                        if (!root?.isConnected) return;
                        for (let kept = root; kept && kept !== document.body; kept = kept.parentElement) {
                            const parent = kept.parentElement;
                            if (!parent) break;
                            for (const sibling of parent.children) {
                                if (sibling !== kept) {
                                    sibling.style.setProperty('display', 'none', 'important');
                                    sibling.style.setProperty('pointer-events', 'none', 'important');
                                }
                            }
                        }
                    });
                });
                window.__biliCinemaFocusObserver.observe(document.body, { childList: true, subtree: true });
            }
            for (const [name, value] of Object.entries({
                position: 'fixed', inset: '0', width: '100vw', height: '100vh',
                'max-width': 'none', 'max-height': 'none', margin: '0',
                'z-index': '2147483647', visibility: 'visible', background: '#000'
            })) player.style.setProperty(name, value, 'important');
            return true;
        })()
        """;
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

        // Keep the private WebView2 session cookies across player reloads. Bilibili
        // may add cookies while the page is open that are not in the QR-login store.
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

        var playerUrl = new Uri($"https://www.bilibili.com/bangumi/play/ep{page.EpisodeId.ToString(CultureInfo.InvariantCulture)}");
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
                    if (ParseNumber(ready) >= 2
                        && string.Equals((await session.InvokeAsync(FocusPlayerScript, cancellationToken)
                            .ConfigureAwait(true)).Trim('"'), "true", StringComparison.OrdinalIgnoreCase))
                    {
                        await session.SetPausedAsync(startPaused, cancellationToken).ConfigureAwait(true);
                        if (startPaused)
                        {
                            await session.InvokeAsync($"if ({Video}) {Video}.muted = false",
                                cancellationToken).ConfigureAwait(true);
                        }
                        // Let the page finish adding its shell before revealing
                        // the WebView in the desktop window.
                        await Task.Delay(250, cancellationToken).ConfigureAwait(true);
                        await session.InvokeAsync(FocusPlayerScript, cancellationToken).ConfigureAwait(true);
                        if (startPaused)
                        {
                            await session.SetPausedAsync(true, cancellationToken).ConfigureAwait(true);
                        }
                        return session;
                    }
                    if (attempt % 20 == 0)
                    {
                        await session.InvokeAsync(FocusPlayerScript, cancellationToken).ConfigureAwait(true);
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

            throw new InvalidOperationException("B 站番剧网页未出现可用播放器，请确认该影片允许网页播放。");
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public async Task<double> GetPositionAsync(CancellationToken cancellationToken)
    {
        // The site can add its header and other navigation after the player loads.
        await InvokeAsync(FocusPlayerScript, cancellationToken).ConfigureAwait(true);
        return ParseNumber(await InvokeAsync($"{Video}?.currentTime ?? 0", cancellationToken)
            .ConfigureAwait(true));
    }

    public async Task<bool> GetPausedAsync(CancellationToken cancellationToken)
    {
        var result = await InvokeAsync($"!!{Video}?.paused", cancellationToken).ConfigureAwait(true);
        return string.Equals(result.Trim('"'), "true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<double> GetSpeedAsync(CancellationToken cancellationToken)
        => ParseNumber(await InvokeAsync($"{Video}?.playbackRate ?? 1", cancellationToken)
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
            await InvokeAsync($"if ({Video}) {{ window.__biliCinemaRemoteEvents ||= {{}}; window.__biliCinemaRemoteEvents.seeked = {{ until: performance.now() + 3000, position: {seconds.ToString("R", CultureInfo.InvariantCulture)} }}; {Video}.currentTime = {seconds.ToString("R", CultureInfo.InvariantCulture)}; }}",
                cancellationToken).ConfigureAwait(true);
        }
    }

    public async Task SetPausedAsync(bool paused, CancellationToken cancellationToken)
    {
        await InvokeAsync(paused
                ? $"if ({Video} && !{Video}.paused) {{ window.__biliCinemaRemoteEvents ||= {{}}; window.__biliCinemaRemoteEvents.pause = {{ until: performance.now() + 800 }}; {Video}.pause(); }}"
                : $"if ({Video}?.paused) {{ window.__biliCinemaRemoteEvents ||= {{}}; window.__biliCinemaRemoteEvents.play = {{ until: performance.now() + 800 }}; {Video}.play().catch(() => {{}}); }}", cancellationToken)
            .ConfigureAwait(true);
    }

    public async Task SetSpeedAsync(double speed, CancellationToken cancellationToken)
    {
        if (double.IsFinite(speed) && speed is >= 0.25 and <= 3)
        {
            await InvokeAsync($"if ({Video} && Math.abs({Video}.playbackRate - {speed.ToString("R", CultureInfo.InvariantCulture)}) > 0.005) {{ window.__biliCinemaRemoteEvents ||= {{}}; window.__biliCinemaRemoteEvents.ratechange = {{ until: performance.now() + 800 }}; {Video}.playbackRate = {speed.ToString("R", CultureInfo.InvariantCulture)}; }}",
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
