using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using DownKyi.Core.Storage;
using DownKyi.ViewModels;

namespace DownKyi.Views;

internal sealed partial class WatchWindow : Window
{
    private readonly WatchWindowViewModel _viewModel;
    private readonly Grid _watchLayout;
    private readonly NativeWebView _browser;
    private readonly ScrollViewer _watchDetails;
    private readonly TextBlock _playbackStatus;
    private readonly Button _fullscreenButton;
    private readonly DispatcherTimer _fullscreenTimer;
    private WindowState _previousWindowState;
    private bool _isFullscreen;
    private bool _browserFullscreen;
    private bool _fullscreenFromBrowser;
    private bool _checkingFullscreen;
    private bool _windowClosing;

    public WatchWindow(WatchWindowViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = _viewModel;
        _watchLayout = this.FindControl<Grid>("WatchLayout")
                       ?? throw new InvalidOperationException("观影布局未加载。");
        _browser = this.FindControl<NativeWebView>("MovieWebView")
                      ?? throw new InvalidOperationException("网页播放器未加载。");
        _watchDetails = this.FindControl<ScrollViewer>("WatchDetails")
                        ?? throw new InvalidOperationException("观影详情未加载。");
        _playbackStatus = this.FindControl<TextBlock>("PlaybackStatus")
                          ?? throw new InvalidOperationException("播放状态未加载。");
        _fullscreenButton = this.FindControl<Button>("FullscreenButton")
                            ?? throw new InvalidOperationException("全屏按钮未加载。");
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
        var seekSlider = this.FindControl<Slider>("SeekSlider")
                         ?? throw new InvalidOperationException("观影进度条未加载。");
        seekSlider.AddHandler(InputElement.PointerPressedEvent,
            (_, _) => _viewModel.BeginSeekDrag(), RoutingStrategies.Tunnel, handledEventsToo: true);
        seekSlider.AddHandler(InputElement.PointerReleasedEvent,
            async (_, _) => await _viewModel.EndSeekDragAsync().ConfigureAwait(true),
            RoutingStrategies.Bubble, handledEventsToo: true);
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
            var result = await _browser.InvokeScript("!!document.fullscreenElement").ConfigureAwait(true);
            if (_windowClosing)
            {
                return;
            }

            var wantsFullscreen = string.Equals(result?.Trim('"'), "true", StringComparison.OrdinalIgnoreCase);
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
            _watchLayout.Margin = new Avalonia.Thickness(24);
            _watchLayout.RowDefinitions = new RowDefinitions("Auto,Auto,*");
            _watchLayout.RowSpacing = 14;
            _browser.Height = 450;
            _watchDetails.IsVisible = true;
            _playbackStatus.IsVisible = true;
            _fullscreenButton.Content = "全屏播放";
            _isFullscreen = false;
        }
        else
        {
            _previousWindowState = WindowState;
            _watchLayout.Margin = new Avalonia.Thickness(0);
            _watchLayout.RowDefinitions = new RowDefinitions("*,Auto,0");
            _watchLayout.RowSpacing = 0;
            _browser.Height = double.NaN;
            _watchDetails.IsVisible = false;
            _playbackStatus.IsVisible = false;
            _fullscreenButton.Content = "退出全屏 (Esc)";
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
