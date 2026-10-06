using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using DownKyi.Core.BiliApi.Login;
using DownKyi.Presentation;

namespace DownKyi.Services.Watch;

internal readonly record struct BilibiliPlayerIdentity(Uri? PageUri, long Cid);
internal readonly record struct BilibiliPlaybackState(double PositionSeconds, double Rate, bool Playing);

// Use Bilibili's normal playback page. Its HTML video supplies the room's
// position and playback controls for both bangumi and ordinary videos.
internal sealed class BilibiliWebPlaybackSession : IDisposable
{
    private const string ViewportScript = """
        (() => {
            if (!document.body) return;
            if (!document.getElementById('bili-cinema-viewport-style')) {
                const style = document.createElement('style');
                style.id = 'bili-cinema-viewport-style';
                style.textContent = `
                    [data-bili-cinema-player] { display: flex !important; flex-direction: column !important; }
                    [data-bili-cinema-player] .bpx-player-primary-area {
                        flex: 1 1 0 !important; min-height: 0 !important; height: auto !important;
                    }
                    [data-bili-cinema-player] .bpx-player-sending-area { flex: 0 0 auto !important; }
                    html[data-bili-cinema-fullscreen] .bpx-player-sending-area,
                    html[data-bili-cinema-fullscreen] .bilibili-player-video-sendbar {
                        display: none !important;
                    }
                    html[data-bili-cinema-fullscreen], html[data-bili-cinema-fullscreen] body {
                        background: #000 !important;
                    }
                `;
                (document.head || document.documentElement).appendChild(style);
            }
            if (!window.__biliCinemaUpdateViewport) {
                window.__biliCinemaUpdateViewport = () => {
                    const fullscreen = !!(window.__biliCinemaAppFullscreen || document.fullscreenElement);
                    const before = document.documentElement.hasAttribute('data-bili-cinema-fullscreen');
                    document.documentElement.toggleAttribute('data-bili-cinema-fullscreen', fullscreen);
                    if (before !== fullscreen) {
                        window.__biliCinemaLayoutSignature = null;
                        requestAnimationFrame(() => window.dispatchEvent(new Event('resize')));
                    }
                };
                document.addEventListener('fullscreenchange', window.__biliCinemaUpdateViewport);
            }
            window.__biliCinemaUpdateViewport();
        })();
        """;
    private const string MiniPlayerSelector = ".bpx-player-miniplayer, .bpx-player-miniplayer-container, .bpx-player-miniplayer-wrap, .bilibili-player-miniplayer, .bilibili-player-miniplayer-container";
    private const string Video = "(() => { const mini = '" + MiniPlayerSelector + "'; const videos = [...document.querySelectorAll('#bilibili-player video, #bilibiliPlayer video, .bpx-player-container video, video')].filter(video => { const rect = video.getBoundingClientRect(); const style = getComputedStyle(video); return !video.closest(mini) && rect.width >= 160 && rect.height >= 90 && style.display !== 'none' && style.visibility !== 'hidden' && Number(style.opacity) > 0; }); const byArea = (left, right) => { const a = left.getBoundingClientRect(); const b = right.getBoundingClientRect(); return b.width * b.height - a.width * a.height; }; return videos.sort(byArea)[0] || null; })()";
    private const string FocusPlayerScript = $$"""
        (() => {
            const video = {{Video}};
            if (!video) {
                document.documentElement.style.background = '#000';
                return false;
            }
            // Prefer the actual inline player shell nearest this video. The outer
            // #bilibili-player can also contain Bilibili's detached mini-player;
            // stretching that outer shell leaves the video floating at mini size.
            const miniPlayerSelector = '{{MiniPlayerSelector}}';
            let player = video.closest('.bpx-player-container, .bilibili-player')
                || video.closest('#bilibili-player, #bilibiliPlayer')
                || video.closest('.player-container, .player-wrap');
            if (!player) {
                for (let parent = video.parentElement; parent && parent !== document.body; parent = parent.parentElement) {
                    const box = parent.getBoundingClientRect();
                    if (box.width >= 320 && box.height >= 180
                        && box.width < window.innerWidth * 2 && box.height < window.innerHeight * 2) {
                        player = parent;
                        break;
                    }
                }
            }
            if (!player) return false;
            window.__biliCinemaPlayerRoot = player;
            player.setAttribute('data-bili-cinema-player', '');
            {{ViewportScript}}
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
            video.disablePictureInPicture = true;
            if (document.pictureInPictureElement === video) {
                document.exitPictureInPicture?.().catch(() => {});
            }
            // Disable site mini-player/PiP wrappers while keeping the normal
            // player controls inside the selected inline player root.
            const disableMiniPlayer = () => {
                const activePlayer = window.__biliCinemaPlayerRoot;
                for (const mini of document.querySelectorAll(
                    miniPlayerSelector)) {
                    if (mini !== activePlayer && !mini.contains(activePlayer)) {
                        if (mini.style.getPropertyValue('display') !== 'none')
                            mini.style.setProperty('display', 'none', 'important');
                        if (mini.style.getPropertyValue('pointer-events') !== 'none')
                            mini.style.setProperty('pointer-events', 'none', 'important');
                    }
                }
            };
            disableMiniPlayer();
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
                const enforceFocus = () => {
                    const root = window.__biliCinemaPlayerRoot;
                    if (!root?.isConnected) return;
                    disableMiniPlayer();
                    for (let kept = root; kept && kept !== document.body; kept = kept.parentElement) {
                        const parent = kept.parentElement;
                        if (!parent) break;
                        for (const sibling of parent.children) {
                            if (sibling === kept) continue;
                            if (sibling.style.getPropertyValue('display') !== 'none'
                                || sibling.style.getPropertyPriority('display') !== 'important') {
                                sibling.style.setProperty('display', 'none', 'important');
                            }
                            if (sibling.style.getPropertyValue('pointer-events') !== 'none'
                                || sibling.style.getPropertyPriority('pointer-events') !== 'important') {
                                sibling.style.setProperty('pointer-events', 'none', 'important');
                            }
                        }
                    }
                    // Compare CSSOM-normalized values. Comparing '0' with '0px'
                    // or '#000' with 'rgb(...)' makes this observer trigger itself.
                    for (const [name, value] of Object.entries({
                        position: 'fixed', inset: '0px', width: '100vw', height: '100vh',
                        'max-width': 'none', 'max-height': 'none', margin: '0px',
                        'z-index': '2147483647', visibility: 'visible', background: 'rgb(0, 0, 0)', transform: 'none'
                    })) {
                        if (root.style.getPropertyValue(name) !== value
                            || root.style.getPropertyPriority(name) !== 'important') {
                            root.style.setProperty(name, value, 'important');
                        }
                    }
                };
                window.__biliCinemaFocusObserver = new MutationObserver(enforceFocus);
                window.__biliCinemaFocusObserver.observe(document.body, {
                    childList: true, subtree: true, attributes: true, attributeFilter: ['class', 'style']
                });
                enforceFocus();
            }
            for (let kept = video; kept && kept !== player; kept = kept.parentElement) {
                for (const [name, value] of Object.entries({
                    width: '100%', height: '100%', 'max-width': 'none', 'max-height': 'none',
                    margin: '0', 'object-fit': 'contain'
                })) {
                    if (name === 'height' && kept.classList.contains('bpx-player-primary-area')) {
                        kept.style.removeProperty('height');
                        continue;
                    }
                    kept.style.setProperty(name, value, 'important');
                }
            }
            for (const [name, value] of Object.entries({
                position: 'fixed', inset: '0px', width: '100vw', height: '100vh',
                'max-width': 'none', 'max-height': 'none', margin: '0px',
                'z-index': '2147483647', visibility: 'visible', background: 'rgb(0, 0, 0)', transform: 'none'
            })) player.style.setProperty(name, value, 'important');
            // Bilibili caches its control-bar geometry. Changing only CSS while
            // WebView2 is hidden does not trigger the same layout pass as F/fullscreen.
            // Notify the site once per actual viewport/root change, then wait for
            // the controls to mount before declaring the player ready.
            if (window.__biliCinemaLayoutRoot !== player) {
                window.__biliCinemaResizeObserver?.disconnect();
                window.__biliCinemaLayoutRoot = player;
                window.__biliCinemaLayoutSignature = null;
                window.__biliCinemaResizeObserver = new ResizeObserver(() => {
                    window.__biliCinemaLayoutSignature = null;
                });
                window.__biliCinemaResizeObserver.observe(player);
            }
            const box = player.getBoundingClientRect();
            const controls = player.querySelector('.bpx-player-control-wrap, .bilibili-player-video-control');
            const signature = [window.innerWidth, window.innerHeight, box.width, box.height, !!controls].join(':');
            if (window.__biliCinemaLayoutSignature !== signature) {
                window.__biliCinemaLayoutSignature = signature;
                window.__biliCinemaLayoutReady = false;
                requestAnimationFrame(() => {
                    window.dispatchEvent(new Event('resize'));
                    requestAnimationFrame(() => {
                        if (window.__biliCinemaLayoutSignature === signature)
                            window.__biliCinemaLayoutReady = true;
                    });
                });
            }
            return !!controls;
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

    internal static string BuildViewportScript(bool fullscreen) =>
        "window.__biliCinemaAppFullscreen = " + (fullscreen ? "true;" : "false;") + ViewportScript;

    public static async Task<BilibiliWebPlaybackSession> StartAsync(
        NativeWebView browser, VideoPage page, bool startPaused, CancellationToken cancellationToken)
    {
        if (page.EpisodeId <= 0 && (page.Cid <= 0 ||
            (string.IsNullOrEmpty(page.Bvid) && page.Avid <= 0)))
        {
            throw new InvalidOperationException("此链接没有可播放的 B 站视频页面。");
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
                "/", cookie.Domain!)
            {
                Secure = true
            });
        }

        var playerUrl = BuildPlayerUri(page);
        var session = new BilibiliWebPlaybackSession(browser, playerUrl);
        try
        {
            browser.Navigate(playerUrl);
            var revealed = false;
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
                        if (!revealed)
                        {
                            // Showing the native surface can update its client size.
                            // Keep the focused page and remeasure after that transition.
                            await session.SetPausedAsync(startPaused, cancellationToken).ConfigureAwait(true);
                            browser.IsVisible = true;
                            revealed = true;
                            await session.InvokeAsync("window.__biliCinemaLayoutSignature = null",
                                cancellationToken).ConfigureAwait(true);
                            await Task.Delay(100, cancellationToken).ConfigureAwait(true);
                            continue;
                        }
                        if (!string.Equals((await session.InvokeAsync(
                            "window.__biliCinemaLayoutReady === true", cancellationToken)
                            .ConfigureAwait(true)).Trim('"'), "true", StringComparison.OrdinalIgnoreCase))
                        {
                            await Task.Delay(100, cancellationToken).ConfigureAwait(true);
                            continue;
                        }
                        await session.SetPausedAsync(startPaused, cancellationToken).ConfigureAwait(true);
                        if (startPaused)
                        {
                            await session.InvokeAsync($"if ({Video}) {Video}.muted = false",
                                cancellationToken).ConfigureAwait(true);
                        }
                        var focusConfirmed = await session.InvokeAsync(FocusPlayerScript, cancellationToken)
                            .ConfigureAwait(true);
                        if (!string.Equals(focusConfirmed.Trim('"'), "true", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
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

            throw new InvalidOperationException("B 站网页未出现可用播放器，请确认该视频允许网页播放。");
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    internal static Uri BuildPlayerUri(VideoPage page)
        => page.EpisodeId > 0
            ? new Uri($"https://www.bilibili.com/bangumi/play/ep{page.EpisodeId.ToString(CultureInfo.InvariantCulture)}")
            : new Uri($"https://www.bilibili.com/video/{(string.IsNullOrEmpty(page.Bvid) ? $"av{page.Avid.ToString(CultureInfo.InvariantCulture)}" : page.Bvid)}?p={Math.Max(1, page.Page).ToString(CultureInfo.InvariantCulture)}");

    public async Task<double> GetPositionAsync(CancellationToken cancellationToken)
    {
        // The site can add its header and other navigation after the player loads.
        await InvokeAsync(FocusPlayerScript, cancellationToken).ConfigureAwait(true);
        return ParseNumber(await InvokeAsync($"{Video}?.currentTime ?? 0", cancellationToken)
            .ConfigureAwait(true));
    }

    public async Task<BilibiliPlaybackState> GetPlaybackStateAsync(CancellationToken cancellationToken)
    {
        var json = await InvokeAsync($"(() => {{ const v = {Video}; if (!v) return null; return {{ position: v.currentTime, rate: v.playbackRate, playing: !v.paused }}; }})()",
            cancellationToken).ConfigureAwait(true);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("播放器状态暂不可用，请稍后重试。");
        }
        return new BilibiliPlaybackState(root.GetProperty("position").GetDouble(),
            root.GetProperty("rate").GetDouble(), root.GetProperty("playing").GetBoolean());
    }

    public async Task<BilibiliPlayerIdentity> GetPageIdentityAsync(CancellationToken cancellationToken)
    {
        const string script = """
            (() => {
                const players = [window.player, window.bilibiliPlayer];
                let cid = 0;
                for (const player of players) {
                    if (typeof player?.getCid !== 'function') continue;
                    try {
                        const value = Number(player.getCid());
                        if (Number.isSafeInteger(value) && value > 0) { cid = value; break; }
                    } catch {
                        // The site may replace its player while changing episodes.
                        // Keep the URL signal when the optional CID probe is unavailable.
                        cid = 0;
                    }
                }
                return { href: location.href, cid };
            })()
            """;
        var result = await InvokeAsync(script, cancellationToken).ConfigureAwait(true);
        using var document = JsonDocument.Parse(result);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("href", out var href)
            || href.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("网页播放器的媒体状态暂不可用。");
        }

        var address = href.GetString();
        var uri = Uri.TryCreate(address, UriKind.Absolute, out var parsed) ? parsed : null;
        var cid = root.TryGetProperty("cid", out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var parsedCid) && parsedCid > 0 ? parsedCid : 0;
        return new BilibiliPlayerIdentity(uri, cid);
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
            _browser.IsVisible = false;
            _browser.Navigate(new Uri("about:blank"));
        }
        catch (Exception error) when (error is InvalidOperationException
            or ObjectDisposedException or System.Runtime.InteropServices.COMException)
        {
            // Closing the window can destroy WebView2 before this callback runs.
        }
    }
}
