using DownKyi.Application.Diagnostics;
using DownKyi.Application.Lifetime;
using DownKyi.Composition;
using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.Core.Settings;
using DownKyi.Desktop.Composition;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Infrastructure.Logging;
using DownKyi.Services.Account;
using DownKyi.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DownKyi.Desktop.Tests;

public sealed class WatchWindowInteractionTests
{
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public Task PendingDownloadAndPlayerPreparationDoNotBlockNavigationOrReloadDownloads()
    {
        return AvaloniaTestDispatcher.RunAsync(async () =>
        {
            var harness = CreateHarness();
            await using var cleanup = harness.ConfigureAwait(true);
            var viewModel = harness.ViewModel;
            var toolsReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var playerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            SetField(viewModel, "_downloadToolsPreparation", toolsReady.Task);
            SetField(viewModel, "_playerPreparation", playerReady.Task);
            SetField(viewModel, "_playerLoading", true);
            try
            {
                var originalContent = viewModel.DownloadContent.MainContent;
                var opening = viewModel.ShowDownloadCommand.ExecuteAsync(null);
                Assert.True(viewModel.ShowDownload);
                Assert.True(viewModel.ShowPreparationProgress);

                await viewModel.ShowHomeCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(2))
                    .ConfigureAwait(true);
                Assert.True(viewModel.ShowHome);
                Assert.False(viewModel.ShowPreparationProgress);
                Assert.True(viewModel.ShowHomeCommand.CanExecute(null));

                toolsReady.SetResult();
                await opening.ConfigureAwait(true);
                Assert.True(viewModel.ShowHome);
                Assert.Same(originalContent, viewModel.DownloadContent.MainContent);

                await viewModel.ShowDownloadCommand.ExecuteAsync(null).ConfigureAwait(true);
                var loadedContent = viewModel.DownloadContent.MainContent;
                Assert.NotNull(loadedContent);
                Assert.False(viewModel.ShowPreparationProgress);
                await viewModel.ShowHomeCommand.ExecuteAsync(null).ConfigureAwait(true);
                await viewModel.ShowDownloadCommand.ExecuteAsync(null).ConfigureAwait(true);
                Assert.Same(loadedContent, viewModel.DownloadContent.MainContent);
                Assert.False(viewModel.ShowPreparationProgress);
            }
            finally
            {
                toolsReady.TrySetResult();
                playerReady.TrySetResult();
                SetField(viewModel, "_playerLoading", false);
            }
        });
    }

    private static void SetField(WatchWindowViewModel viewModel, string name, object value)
    {
        var field = typeof(WatchWindowViewModel).GetField(name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(viewModel, value);
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public Task AccountNavigationInputClearingAndDockedChatStateStayWired()
    {
        return AvaloniaTestDispatcher.RunAsync(async () =>
        {
            var harness = CreateHarness();
            await using var cleanup = harness.ConfigureAwait(true);
            var viewModel = harness.ViewModel;

            await viewModel.InitializeAsync().ConfigureAwait(true);

            Assert.True(viewModel.IsLoggedIn);
            Assert.Equal("Test User", viewModel.AccountName);
            Assert.Equal("123456", viewModel.AccountUid);
            Assert.Equal("https://example.invalid/avatar.png", viewModel.AccountAvatar);

            Assert.True(viewModel.ShowHome);
            viewModel.RoomVideoInput = "https://www.bilibili.com/video/BV1xx411c7mD";
            viewModel.InviteText = "https://localhost/invite/test";
            viewModel.ClearVideoInputCommand.Execute(null);
            viewModel.ClearInviteCommand.Execute(null);
            Assert.Empty(viewModel.RoomVideoInput);
            Assert.Empty(viewModel.InviteText);

            viewModel.ShowLoginCommand.Execute(null);
            Assert.True(viewModel.ShowLogin);
            viewModel.ShowHomeCommand.Execute(null);
            Assert.True(viewModel.ShowHome);

            viewModel.ChatDraft = "保留这条聊天草稿";
            var initiallyOpen = viewModel.DockedChatOpen;
            viewModel.ToggleDockedChatCommand.Execute(null);
            Assert.NotEqual(initiallyOpen, viewModel.DockedChatOpen);
            Assert.Equal("保留这条聊天草稿", viewModel.ChatDraft);
        });
    }

    private static WatchHarness CreateHarness()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bilicinema-watch-interaction-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
        settings.Update(current => current with
        {
            About = current.About with { AutoUpdateWhenLaunch = AllowStatus.No }
        });
        var logProvider = new ApplicationLogProvider(new ApplicationLogOptions(Path.Combine(directory, "logs")));
        var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logProvider));
        var databasePath = Path.Combine(directory, "downkyi.db");
        var host = DownKyiHost.Create(services =>
        {
            services.AddDownKyiWatch(loggerFactory, logProvider);
            services.Replace(ServiceDescriptor.Singleton<ISettingsStore>(settings));
            services.Replace(ServiceDescriptor.Singleton(new SqliteDownloadTaskStoreOptions(databasePath)));
            services.Replace(ServiceDescriptor.Singleton<IUserSessionCoordinator>(new LoggedInTestSession()));
            services.Replace(ServiceDescriptor.Singleton<IApplicationLifecycle>(new TestApplicationLifecycle()));
        });

        return new WatchHarness(directory, settings, logProvider, loggerFactory, host,
            host.Services.GetRequiredService<WatchWindowViewModel>());
    }

    private sealed class WatchHarness(
        string directory,
        SettingsStore settings,
        ApplicationLogProvider logProvider,
        ILoggerFactory loggerFactory,
        IHost host,
        WatchWindowViewModel viewModel) : IAsyncDisposable
    {
        public WatchWindowViewModel ViewModel { get; } = viewModel;

        public async ValueTask DisposeAsync()
        {
            await ViewModel.DisposeAsync().ConfigureAwait(true);
            await ((IAsyncDisposable)host).DisposeAsync().ConfigureAwait(true);
            loggerFactory.Dispose();
            await logProvider.DisposeAsync().ConfigureAwait(true);
            await settings.DisposeAsync().ConfigureAwait(true);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class LoggedInTestSession : IUserSessionCoordinator
    {
        public Task<UserSessionSnapshot> RefreshAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new UserSessionSnapshot(
                new UserInfoForNavigation
                {
                    IsLogin = true,
                    Mid = 123456,
                    Name = "Test User",
                    Face = "https://example.invalid/avatar.png"
                },
                HasLoginFile: true));
        }
    }

    private sealed class TestApplicationLifecycle : IApplicationLifecycle
    {
        public Task RequestShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ExitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> RestartAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}
