using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using DownKyi.ViewModels;

namespace DownKyi.Views;

internal sealed partial class WatchWindow : Window
{
    private readonly WatchWindowViewModel _viewModel;

    public WatchWindow(WatchWindowViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = _viewModel;
        var browser = this.FindControl<NativeWebView>("MovieWebView")
                      ?? throw new InvalidOperationException("网页播放器未加载。");
        browser.EnvironmentRequested += (_, args) =>
        {
            if (args is WindowsWebView2EnvironmentRequestedEventArgs windows)
            {
                windows.IsInPrivateModeEnabled = true;
                windows.AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required";
            }
        };
        browser.NavigationStarted += (_, args) =>
        {
            var url = args.Request;
            if (url == null || url.Scheme != "about" && !(url.Scheme == Uri.UriSchemeHttps
                    && url.Host.Equals("player.bilibili.com", StringComparison.OrdinalIgnoreCase)
                    && url.AbsolutePath.Equals("/player.html", StringComparison.Ordinal)))
            {
                args.Cancel = true;
            }
        };
        browser.NewWindowRequested += (_, args) => args.Handled = true;
        _viewModel.AttachBrowser(browser);
        var seekSlider = this.FindControl<Slider>("SeekSlider")
                         ?? throw new InvalidOperationException("观影进度条未加载。");
        seekSlider.AddHandler(InputElement.PointerPressedEvent,
            (_, _) => _viewModel.BeginSeekDrag(), RoutingStrategies.Tunnel, handledEventsToo: true);
        seekSlider.AddHandler(InputElement.PointerReleasedEvent,
            async (_, _) => await _viewModel.EndSeekDragAsync().ConfigureAwait(true),
            RoutingStrategies.Bubble, handledEventsToo: true);
        Opened += OnOpened;
        Closing += (_, _) => _viewModel.StopForWindowClose();
    }

    private async void OnOpened(object? sender, EventArgs args)
    {
        Opened -= OnOpened;
        await _viewModel.InitializeAsync().ConfigureAwait(true);
    }
}
