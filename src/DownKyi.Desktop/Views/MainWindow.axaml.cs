using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using DownKyi.Application.Diagnostics;
using DownKyi.Application.Lifetime;
using DownKyi.Core.Settings;
using DownKyi.Core.Settings.Models;
using DownKyi.ViewModels;
using Microsoft.Extensions.Logging;

namespace DownKyi.Views;

internal partial class MainWindow : Window
{
    private readonly IApplicationLifecycle _applicationLifecycle;
    private readonly ILogger<MainWindow> _logger;
    private readonly ISettingsStore _settingsStore;
    private WindowSettings _windowSettings;
    private bool _closeConfirmed;
    private bool _closeInProgress;

    public MainWindow(
        MainWindowViewModel viewModel,
        ISettingsStore settingsStore,
        IApplicationLifecycle applicationLifecycle,
        ILogger<MainWindow> logger)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _applicationLifecycle = applicationLifecycle
            ?? throw new ArgumentNullException(nameof(applicationLifecycle));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        InitializeComponent();
        var window = _settingsStore.Current.Window;
        _windowSettings = new WindowSettings
        {
            Width = window.Width,
            Height = window.Height,
            X = window.X,
            Y = window.Y
        };
        ApplyWindowSettings();
        DataContext = viewModel;
    }

    private void ApplyWindowSettings()
    {
        Width = _windowSettings.Width;
        Height = _windowSettings.Height;
        if (double.IsNaN(_windowSettings.X) || double.IsNaN(_windowSettings.Y))
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            Position = new PixelPoint((int)_windowSettings.X, (int)_windowSettings.Y);
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        EnsureWindowFitsWorkingArea();
        base.OnOpened(e);
    }

    private void EnsureWindowFitsWorkingArea()
    {
        var screen = Screens.ScreenFromWindow(this)
            ?? Screens.ScreenFromPoint(Position)
            ?? Screens.Primary;
        if (screen is null || FrameSize is not { } frameSize)
        {
            return;
        }

        var placement = ConstrainRestoredPlacement(
            Position,
            ClientSize,
            frameSize,
            screen.WorkingArea,
            screen.Scaling);
        Width = placement.ClientSize.Width;
        Height = placement.ClientSize.Height;
        Position = placement.Position;
    }

    internal static (PixelPoint Position, Size ClientSize) ConstrainRestoredPlacement(
        PixelPoint position,
        Size clientSize,
        Size frameSize,
        PixelRect workingArea,
        double scaling)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(scaling, 0);

        var decorationWidth = Math.Max(0, frameSize.Width - clientSize.Width);
        var decorationHeight = Math.Max(0, frameSize.Height - clientSize.Height);
        var maxClientWidth = Math.Max(0, workingArea.Width / scaling - decorationWidth);
        var maxClientHeight = Math.Max(0, workingArea.Height / scaling - decorationHeight);
        var constrainedClientSize = new Size(
            Math.Min(clientSize.Width, maxClientWidth),
            Math.Min(clientSize.Height, maxClientHeight));
        var frameWidthInPixels = (int)Math.Ceiling(
            (constrainedClientSize.Width + decorationWidth) * scaling);
        var frameHeightInPixels = (int)Math.Ceiling(
            (constrainedClientSize.Height + decorationHeight) * scaling);
        var maxX = Math.Max(workingArea.X, workingArea.Right - frameWidthInPixels);
        var maxY = Math.Max(workingArea.Y, workingArea.Bottom - frameHeightInPixels);

        return (
            new PixelPoint(
                Math.Clamp(position.X, workingArea.X, maxX),
                Math.Clamp(position.Y, workingArea.Y, maxY)),
            constrainedClientSize);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (Design.IsDesignMode || _closeConfirmed)
        {
            SaveWindowSettings();
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        if (_closeInProgress)
        {
            return;
        }

        _closeInProgress = true;
        SaveWindowSettings();
        ObserveCloseCompletion(CompleteCloseAsync());
    }

    private void SaveWindowSettings()
    {
        if (Design.IsDesignMode)
        {
            return;
        }

        if (WindowState == WindowState.Normal)
        {
            _windowSettings.Width = Width;
            _windowSettings.Height = Height;
            _windowSettings.X = Position.X;
            _windowSettings.Y = Position.Y;
        }

        _settingsStore.Update(settings => settings with
        {
            Window = new WindowApplicationSettings(
                _windowSettings.Width,
                _windowSettings.Height,
                _windowSettings.X,
                _windowSettings.Y)
        });
    }

    private async Task CompleteCloseAsync()
    {
        try
        {
            await _applicationLifecycle.RequestShutdownAsync().ConfigureAwait(true);
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
                "Application shutdown failed while closing the main window.",
                completed.Exception!.GetBaseException()),
            System.Threading.CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }
}
