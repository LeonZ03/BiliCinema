using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using DownKyi.Core.Storage;
using DownKyi.ViewModels;

namespace DownKyi.Views;

internal sealed partial class WatchWindow : Window
{
    private const string FullscreenStateScript = """
        (() => {
            if (!window.__biliCinemaEscapeHook) {
                window.__biliCinemaEscapeHook = true;
                window.addEventListener('keydown', event => {
                    if (event.key === 'Escape') window.__biliCinemaEscapeRequested = true;
                }, true);
            }
            const exit = window.__biliCinemaEscapeRequested === true;
            window.__biliCinemaEscapeRequested = false;
            return (document.fullscreenElement ? 1 : 0) + (exit ? 2 : 0);
        })()
        """;
    private readonly WatchWindowViewModel _viewModel;
    private readonly Grid _watchLayout;
    private readonly Border _sidebar;
    private readonly Border _playerSurface;
    private readonly NativeWebView _browser;
    private readonly Button _fullscreenButton;
    private readonly DispatcherTimer _fullscreenTimer;
    private WindowState _previousWindowState;
    private bool _isFullscreen;
    private bool _browserFullscreen;
    private bool _fullscreenFromBrowser;
    private bool _checkingFullscreen;
    private bool _windowClosing;
    private readonly IBrush? _normalBackground;
    private readonly Avalonia.CornerRadius _normalCornerRadius;

    public WatchWindow(WatchWindowViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = _viewModel;
        _watchLayout = this.FindControl<Grid>("WatchLayout")
                       ?? throw new InvalidOperationException("观影布局未加载。");
        _sidebar = this.FindControl<Border>("Sidebar")
                   ?? throw new InvalidOperationException("导航栏未加载。");
        _playerSurface = this.FindControl<Border>("PlayerSurface")
                         ?? throw new InvalidOperationException("播放器容器未加载。");
        _browser = this.FindControl<NativeWebView>("MovieWebView")
                      ?? throw new InvalidOperationException("网页播放器未加载。");
        _fullscreenButton = this.FindControl<Button>("FullscreenButton")
                            ?? throw new InvalidOperationException("全屏按钮未加载。");
        _normalBackground = Background;
        _normalCornerRadius = _playerSurface.CornerRadius;
        _browser.EnvironmentRequested += (_, args) =>
        {
            args.EnableDevTools = false;
            if (args is WindowsWebView2EnvironmentRequestedEventArgs windows)
            {
                windows.IsInPrivateModeEnabled = true;
                windows.UserDataFolder = Path.Combine(ApplicationStorage.GetRoot(), "WebView2");
                windows.ProfileName = "BiliCinemaWatch";
                windows.AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required";
            }
        };
        _browser.NavigationStarted += (_, args) =>
        {
            var url = args.Request;
            if (url == null || url.Scheme != "about" && !(url.Scheme == Uri.UriSchemeHttps
                    && url.Host.Equals("www.bilibili.com", StringComparison.OrdinalIgnoreCase)
                    && url.AbsolutePath.StartsWith("/bangumi/play/ep", StringComparison.Ordinal)
                    && long.TryParse(url.AbsolutePath.AsSpan("/bangumi/play/ep".Length), out var episodeId)
                    && episodeId > 0))
            {
                args.Cancel = true;
            }
        };
        _browser.NewWindowRequested += (_, args) => args.Handled = true;
        _viewModel.AttachBrowser(_browser);
        _fullscreenTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _fullscreenTimer.Tick += OnFullscreenTimerTick;
        Opened += OnOpened;
        Closing += (_, _) =>
        {
            _windowClosing = true;
            _fullscreenTimer.Stop();
            _viewModel.StopForWindowClose();
        };
    }

    private async void OnFullscreenClick(object? sender, RoutedEventArgs args)
    {
        var exitBrowserFullscreen = _isFullscreen && _browserFullscreen;
        _fullscreenFromBrowser = false;
        ToggleFullscreen();
        if (exitBrowserFullscreen)
        {
            try
            {
                await _browser.InvokeScript("document.exitFullscreen?.()").ConfigureAwait(true);
            }
            catch (Exception error) when (error is InvalidOperationException
                or ObjectDisposedException or System.Runtime.InteropServices.COMException)
            {
                // The page may already have left fullscreen or be navigating.
            }
        }
    }

    protected override void OnKeyDown(KeyEventArgs args)
    {
        if ((_isFullscreen && args.Key == Key.Escape) || args.Key == Key.F11)
        {
            ToggleFullscreen();
            _fullscreenFromBrowser = false;
            args.Handled = true;
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
        try
        {
            var result = await _browser.InvokeScript(FullscreenStateScript).ConfigureAwait(true);
            if (_windowClosing)
            {
                return;
            }

            var state = int.TryParse(result?.Trim('"'), out var parsed) ? parsed : 0;
            var wantsFullscreen = (state & 1) != 0;
            if ((state & 2) != 0 && _isFullscreen)
            {
                _browserFullscreen = wantsFullscreen;
                _fullscreenFromBrowser = false;
                ToggleFullscreen();
                if (wantsFullscreen)
                {
                    await _browser.InvokeScript("document.exitFullscreen?.()").ConfigureAwait(true);
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
            _checkingFullscreen = false;
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
            _playerSurface.CornerRadius = _normalCornerRadius;
            _browser.Height = 450;
            _fullscreenButton.Content = "全屏播放";
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
            _playerSurface.CornerRadius = new Avalonia.CornerRadius(0);
            _browser.Height = double.NaN;
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
