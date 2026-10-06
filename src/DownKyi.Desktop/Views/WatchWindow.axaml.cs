using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using DownKyi.Application.Diagnostics;
using DownKyi.Application.Lifetime;
using DownKyi.Core.Storage;
using DownKyi.Services.Watch;
using DownKyi.ViewModels;
using Microsoft.Extensions.Logging;

namespace DownKyi.Views;

internal sealed partial class WatchWindow : Window
{
    private static readonly Action<ILogger, Exception?> ChatOverlayWarning =
        LoggerMessage.Define(LogLevel.Warning, new EventId(1, "RoomChatOverlay"),
            "Room chat overlay could not be refreshed in the player.");

    private const string FullscreenStateScript = """
        (() => {
            if (!window.__biliCinemaEscapeHook) {
                window.__biliCinemaEscapeHook = true;
                window.addEventListener('keydown', event => {
                    if (event.key === 'Escape'
                        && !window.__biliCinemaChatUi?.host?.classList.contains('bc-open'))
                        window.__biliCinemaEscapeRequested = true;
                }, true);
            }
            const exit = window.__biliCinemaEscapeRequested === true;
            window.__biliCinemaEscapeRequested = false;
            const root = window.__biliCinemaPlayerRoot;
            const chat = window.__biliCinemaChatUi;
            const chatMissing = !chat?.host?.isConnected || !root?.contains(chat.host);
            return (document.fullscreenElement ? 1 : 0) + (exit ? 2 : 0)
                + (chatMissing ? 4 : 0);
        })()
        """;
    private const string ToggleDanmakuScript = """
        (() => {
            const control = document.querySelector(
                '.bpx-player-dm-switch, .bpx-player-ctrl-dm, .bilibili-player-video-danmaku-switch');
            if (control) {
                control.click();
                return true;
            }
            return document.body?.dispatchEvent(new KeyboardEvent('keydown', {
                key: 'd', code: 'KeyD', bubbles: true, cancelable: true
            })) ?? false;
        })()
        """;
    private readonly WatchWindowViewModel _viewModel;
    private readonly IApplicationLifecycle _applicationLifecycle;
    private readonly ILogger<WatchWindow> _logger;
    private readonly Grid _watchLayout;
    private readonly Border _sidebar;
    private readonly Border _roomPlayerSurface;
    private readonly NativeWebView _roomBrowser;
    private NativeWebView CurrentBrowser => _roomBrowser;
    private readonly DispatcherTimer _fullscreenTimer;
    private WindowState _previousWindowState;
    private bool _isFullscreen;
    private bool _browserFullscreen;
    private bool _fullscreenFromBrowser;
    private bool _checkingFullscreen;
    private bool _chatOverlayErrorLogged;
    private int _lastChatRevision = -1;
    private int _lastChatToastSequence = -1;
    private int _lastHostActionToastSequence = -1;
    private int _lastChatMemberCount = -1;
    private bool _lastChatFullscreen;
    private bool _lastChatInRoom;
    private bool _windowClosing;
    private bool _closeConfirmed;
    private bool _closeInProgress;
    private readonly IBrush? _normalBackground;
    private readonly Avalonia.CornerRadius _normalCornerRadius;

    public WatchWindow(
        WatchWindowViewModel viewModel,
        IApplicationLifecycle applicationLifecycle,
        ILogger<WatchWindow> logger)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _applicationLifecycle = applicationLifecycle ?? throw new ArgumentNullException(nameof(applicationLifecycle));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        InitializeComponent();
        DataContext = _viewModel;
        _watchLayout = this.FindControl<Grid>("WatchLayout")
                       ?? throw new InvalidOperationException("观影布局未加载。");
        _sidebar = this.FindControl<Border>("Sidebar")
                   ?? throw new InvalidOperationException("导航栏未加载。");
        _roomPlayerSurface = this.FindControl<Border>("RoomPlayerSurface")
                               ?? throw new InvalidOperationException("房间播放器容器未加载。");
        _roomBrowser = this.FindControl<NativeWebView>("RoomMovieWebView")
                       ?? throw new InvalidOperationException("房间播放器未加载。");
        // Keep the native surface sized by its stable Border instead of a
        // second fixed height. This lets fullscreen expand without changing
        // the surrounding Avalonia layout during WebView navigation.
        _roomBrowser.Height = double.NaN;
        _normalBackground = Background;
        _normalCornerRadius = _roomPlayerSurface.CornerRadius;
        ConfigureBrowser(_roomBrowser, "BiliCinemaOnline");
        _viewModel.AttachBrowser(_roomBrowser);
        _fullscreenTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _fullscreenTimer.Tick += OnFullscreenTimerTick;
        Opened += OnOpened;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (Design.IsDesignMode || _closeConfirmed)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        if (_closeInProgress)
        {
            return;
        }

        _closeInProgress = true;
        _windowClosing = true;
        _fullscreenTimer.Stop();
        ObserveCloseCompletion(CompleteCloseAsync());
    }

    private async Task CompleteCloseAsync()
    {
        try
        {
            try
            {
                await _viewModel.DisposeAsync().ConfigureAwait(true);
            }
            finally
            {
                await _applicationLifecycle.RequestShutdownAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            _closeConfirmed = true;
            _closeInProgress = false;
            Close();
        }
    }

    private void ObserveCloseCompletion(Task closeTask)
    {
        _ = closeTask.ContinueWith(
            completed => _logger.LogErrorMessage(
                "BiliCinema cleanup failed while closing the window.",
                completed.Exception!.GetBaseException()),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private static void ConfigureBrowser(NativeWebView browser, string profileName)
    {
        browser.EnvironmentRequested += (_, args) =>
        {
            args.EnableDevTools = false;
            if (args is WindowsWebView2EnvironmentRequestedEventArgs windows)
            {
                windows.IsInPrivateModeEnabled = true;
                windows.UserDataFolder = Path.Combine(ApplicationStorage.GetRoot(), "WebView2");
                windows.ProfileName = profileName;
                windows.AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required";
            }
        };
        browser.NavigationStarted += (_, args) =>
        {
            var url = args.Request;
            var allowedPlaybackNavigation = IsAllowedPlaybackNavigation(url);
            if (allowedPlaybackNavigation)
            {
                // Native WebView2 surfaces do not reliably honor Avalonia Opacity.
                // Hide the native surface before Bilibili paints its page shell;
                // StartAsync reveals it only after the focused video is ready.
                browser.IsVisible = false;
            }

            if (!allowedPlaybackNavigation)
            {
                args.Cancel = true;
            }
        };
        browser.NewWindowRequested += (_, args) => args.Handled = true;
    }

    internal static bool IsAllowedPlaybackNavigation(Uri? url)
    {
        if (url == null)
        {
            return false;
        }

        if (url.AbsoluteUri.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return url.Scheme == Uri.UriSchemeHttps
            && url.Host.Equals("www.bilibili.com", StringComparison.OrdinalIgnoreCase)
            && ((url.AbsolutePath.StartsWith("/bangumi/play/ep", StringComparison.Ordinal)
                    && long.TryParse(url.AbsolutePath.AsSpan("/bangumi/play/ep".Length), out var episodeId)
                    && episodeId > 0)
                || IsVideoPlaybackPath(url.AbsolutePath));
    }

    internal static bool IsVideoPlaybackPath(string path)
    {
        const string prefix = "/video/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var id = path.AsSpan(prefix.Length).TrimEnd('/');
        if (id.Length == 12 && id.StartsWith("BV", StringComparison.Ordinal))
        {
            foreach (var character in id[2..])
            {
                if (!char.IsAsciiLetterOrDigit(character)) return false;
            }
            return true;
        }
        return id.Length > 2 && id[0] == 'a' && id[1] == 'v'
            && long.TryParse(id[2..], out var avid) && avid > 0;
    }

    protected override async void OnKeyDown(KeyEventArgs args)
    {
        var shortcutAvailable = _viewModel.ShowRoom && _viewModel.PlayerReady
            && args.KeyModifiers == KeyModifiers.None
            && FocusManager?.GetFocusedElement() is not TextBox
            && !_viewModel.ChatPanelOpen;
        if (shortcutAvailable && args.Key == Key.D)
        {
            args.Handled = true;
            try
            {
                await CurrentBrowser.InvokeScript(ToggleDanmakuScript).ConfigureAwait(true);
            }
            catch (Exception error) when (error is InvalidOperationException
                or ObjectDisposedException or System.Runtime.InteropServices.COMException)
            {
                // The player may be navigating while the shortcut is pressed.
            }
            return;
        }

        if (!_viewModel.ChatPanelOpen && ((_isFullscreen && args.Key == Key.Escape)
            || args.Key == Key.F11)
            || (shortcutAvailable && args.Key == Key.F))
        {
            var exitBrowserFullscreen = _isFullscreen && _browserFullscreen;
            ToggleFullscreen();
            _fullscreenFromBrowser = false;
            args.Handled = true;
            if (exitBrowserFullscreen)
            {
                try
                {
                    await CurrentBrowser.InvokeScript("document.exitFullscreen?.()").ConfigureAwait(true);
                }
                catch (Exception error) when (error is InvalidOperationException
                    or ObjectDisposedException or System.Runtime.InteropServices.COMException)
                {
                    // The page may already have left fullscreen.
                }
            }
        }

        base.OnKeyDown(args);
    }

    private async void OnFullscreenTimerTick(object? sender, EventArgs args)
    {
        if (_checkingFullscreen || _windowClosing)
        {
            return;
        }

        _checkingFullscreen = true;
        var chatMissing = false;
        try
        {
            var result = await CurrentBrowser.InvokeScript(FullscreenStateScript).ConfigureAwait(true);
            if (_windowClosing)
            {
                return;
            }

            var state = int.TryParse(result?.Trim('"'), out var parsed) ? parsed : 0;
            chatMissing = (state & 4) != 0;
            var wantsFullscreen = (state & 1) != 0;
            if ((state & 2) != 0 && _isFullscreen)
            {
                _browserFullscreen = wantsFullscreen;
                _fullscreenFromBrowser = false;
                ToggleFullscreen();
                if (wantsFullscreen)
                {
                    await CurrentBrowser.InvokeScript("document.exitFullscreen?.()").ConfigureAwait(true);
                }
                return;
            }

            if (wantsFullscreen == _browserFullscreen)
            {
                return;
            }

            _browserFullscreen = wantsFullscreen;
            if (wantsFullscreen && !_isFullscreen)
            {
                ToggleFullscreen();
                _fullscreenFromBrowser = true;
            }
            else if (!wantsFullscreen && _fullscreenFromBrowser)
            {
                if (_isFullscreen)
                {
                    ToggleFullscreen();
                }
                _fullscreenFromBrowser = false;
            }
        }
        catch (Exception error) when (error is InvalidOperationException
            or ObjectDisposedException or System.Runtime.InteropServices.COMException)
        {
            // Navigating the player replaces its JavaScript context.
        }
        finally
        {
            try
            {
                await RefreshChatOverlayAsync(chatMissing).ConfigureAwait(true);
            }
            finally
            {
                _checkingFullscreen = false;
            }
        }
    }

    private async Task RefreshChatOverlayAsync(bool chatMissing)
    {
        var inRoom = _viewModel.IsInRoom;
        var chatRevision = _viewModel.ChatRevision;
        var toastSequence = _viewModel.ChatToastSequence;
        var actionSequence = _viewModel.HostActionToastSequence;
        var memberCount = _viewModel.RoomMemberCount;
        if (_windowClosing || !_viewModel.ShowRoom || !_viewModel.PlayerReady
            || !chatMissing && chatRevision == _lastChatRevision
                && toastSequence == _lastChatToastSequence
                && actionSequence == _lastHostActionToastSequence
                && memberCount == _lastChatMemberCount
                && _isFullscreen == _lastChatFullscreen
                && inRoom == _lastChatInRoom)
        {
            return;
        }

        try
        {
            await CurrentBrowser.InvokeScript(RoomChatOverlayScript.Build(
                _viewModel.ChatMessages,
                _viewModel.LatestChatToast,
                _viewModel.RoomClientId,
                _isFullscreen,
                inRoom,
                memberCount,
                chatRevision,
                toastSequence,
                _viewModel.ChatToastRemainingMilliseconds,
                _viewModel.HostActionNotices,
                actionSequence)).ConfigureAwait(true);
            _lastChatRevision = chatRevision;
            _lastChatToastSequence = toastSequence;
            _lastHostActionToastSequence = actionSequence;
            _lastChatMemberCount = memberCount;
            _lastChatFullscreen = _isFullscreen;
            _lastChatInRoom = inRoom;
        }
        catch (Exception error) when (error is InvalidOperationException
            or ObjectDisposedException or System.Runtime.InteropServices.COMException)
        {
            if (!_windowClosing && !_chatOverlayErrorLogged)
            {
                _chatOverlayErrorLogged = true;
                ChatOverlayWarning(_logger, null);
            }
        }
    }

    private void ToggleFullscreen()
    {
        if (_isFullscreen)
        {
            WindowState = _previousWindowState;
            Background = _normalBackground;
            _sidebar.IsVisible = true;
            _watchLayout.Margin = new Avalonia.Thickness(22);
            _watchLayout.RowDefinitions = new RowDefinitions("Auto,Auto,*");
            _watchLayout.RowSpacing = 14;
            _roomPlayerSurface.CornerRadius = _normalCornerRadius;
            _roomPlayerSurface.Height = 440;
            _isFullscreen = false;
        }
        else
        {
            _previousWindowState = WindowState;
            Background = Brushes.Black;
            _sidebar.IsVisible = false;
            _watchLayout.Margin = new Avalonia.Thickness(0);
            _watchLayout.RowDefinitions = new RowDefinitions("*,0,0");
            _watchLayout.RowSpacing = 0;
            _roomPlayerSurface.CornerRadius = new Avalonia.CornerRadius(0);
            _roomPlayerSurface.Height = double.NaN;
            _isFullscreen = true;
            WindowState = WindowState.FullScreen;
        }
    }

    private async void OnOpened(object? sender, EventArgs args)
    {
        Opened -= OnOpened;
        _fullscreenTimer.Start();
        await _viewModel.InitializeAsync().ConfigureAwait(true);
    }
}
