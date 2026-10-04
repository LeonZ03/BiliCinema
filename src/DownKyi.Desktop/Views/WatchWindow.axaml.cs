using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
