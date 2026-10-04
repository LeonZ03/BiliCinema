using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using Avalonia;
using DownKyi.Platform;

namespace DownKyi.Desktop;

public static class DesktopApplication
{
    public static async Task RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (await ProcessRestartLauncher.RunHelperIfRequestedAsync(args).ConfigureAwait(false))
        {
            return;
        }

        App.WatchMode = args.Contains("--watch", StringComparer.OrdinalIgnoreCase)
                        || string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath),
                            "DownKyi.Watch", StringComparison.OrdinalIgnoreCase);

        var appBuilder = BuildAvaloniaApp();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                await RunOnWindowsStaThreadAsync(
                        () => appBuilder.StartWithClassicDesktopLifetime(args))
                    .ConfigureAwait(false);
            }
            else
            {
                appBuilder.StartWithClassicDesktopLifetime(args);
            }
        }
        finally
        {
            if (appBuilder.Instance is IAsyncDisposable application)
            {
                await application.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    internal static Task RunOnWindowsStaThreadAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var uiTask = new Task(
            action,
            CancellationToken.None,
            TaskCreationOptions.RunContinuationsAsynchronously);
        var uiThread = new Thread(
            () => uiTask.RunSynchronously(TaskScheduler.Default))
        {
            IsBackground = false,
            Name = "DownKyi UI"
        };
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();
        return uiTask;
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .LogToTrace()
#endif
            ;
    }
}
