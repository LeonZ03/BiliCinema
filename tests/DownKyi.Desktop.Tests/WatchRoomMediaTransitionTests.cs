using Avalonia.Headless.XUnit;
using DownKyi.Application.Diagnostics;
using DownKyi.Composition;
using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Core.Settings;
using DownKyi.Desktop.Composition;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Infrastructure.Logging;
using DownKyi.Presentation;
using DownKyi.RoomServer;
using DownKyi.Services;
using DownKyi.Services.Account;
using DownKyi.Services.Video;
using DownKyi.Services.Watch;
using DownKyi.ViewModels;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DownKyi.Desktop.Tests;

public sealed class WatchRoomMediaTransitionTests
{
    private const string OldBvid = "BV1ZpYd66ELP";
    private const string RoomBvid = "BV1xx411c7mD";
    private const string DelayedBvid = "BV1Q541167Qg";

    [AvaloniaFact]
    public Task FailedRoomSelectionClearsOldMediaAndSyncRetriesSameSnapshot()
    {
        return AvaloniaTestDispatcher.RunAsync(async () =>
        {
            var directory = CreateTestDirectory();
            var roomFailuresObserved = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var roomParseCalls = 0;
            var allowRoomParse = false;
            var parser = new VideoParseCoordinator((input, _) =>
            {
                if (input.Contains(RoomBvid, StringComparison.Ordinal))
                {
                    var call = Interlocked.Increment(ref roomParseCalls);
                    var shouldFail = !Volatile.Read(ref allowRoomParse);
                    if (shouldFail && call >= 2)
                        roomFailuresObserved.TrySetResult();
                    return Task.FromResult<IInfoService?>(shouldFail
                        ? DetailService("Room title", [])
                        : DetailService("Room title", Page(RoomBvid, 2202, 2202, "Room page")));
                }

                if (input.Contains(OldBvid, StringComparison.Ordinal))
                    return Task.FromResult<IInfoService?>(DetailService("Old title", Page(OldBvid, 1101, 1, "Old page")));

                return Task.FromResult<IInfoService?>(null);
            });

            var harness = CreateHarness(directory, parser);
            var app = CreateRoomServer();
            try
            {
                await app.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
                var address = WebSocketAddress(app);
                var hostClient = new WatchRoomClient();
                await using var hostLifetime = hostClient.ConfigureAwait(true);
                await hostClient.ConnectAsync(address, create: true, roomCode: null,
                    TestContext.Current.CancellationToken).ConfigureAwait(true);
                await hostClient.SendAsync(new
                {
                    type = "select",
                    media = new { bvid = RoomBvid, aid = 2202, cid = 2202 }
                }, TestContext.Current.CancellationToken).ConfigureAwait(true);

                var viewModel = harness.ViewModel;
                await viewModel.InitializeAsync().ConfigureAwait(true);
                viewModel.RoomVideoInput = $"https://www.bilibili.com/video/{OldBvid}";
                await viewModel.ParseCommand.ExecuteAsync(null).ConfigureAwait(true);
                Assert.Equal("Old title · Old page", viewModel.RoomFilmTitle);

                viewModel.ServiceAddress = address;
                viewModel.InviteText = hostClient.RoomCode!;
                await viewModel.JoinRoomCommand.ExecuteAsync(null).ConfigureAwait(true);
                await roomFailuresObserved.Task.WaitAsync(TimeSpan.FromSeconds(5),
                    TestContext.Current.CancellationToken).ConfigureAwait(true);

                Assert.True(viewModel.IsInRoom);
                Assert.Empty(viewModel.RoomFilmTitle);
                Assert.False(viewModel.PlayerReady);
                Assert.True(roomParseCalls >= 2);

                Volatile.Write(ref allowRoomParse, true);
                await viewModel.SyncPlaybackCommand.ExecuteAsync(null).ConfigureAwait(true);

                Assert.Equal("Room title · Room page", viewModel.RoomFilmTitle);
                Assert.Equal($"https://www.bilibili.com/video/{RoomBvid}", viewModel.RoomVideoInput);
                Assert.False(viewModel.PlayerReady);
                Assert.True(roomParseCalls >= 3);
            }
            finally
            {
                try
                {
                    await harness.DisposeAsync().ConfigureAwait(true);
                }
                finally
                {
                    try
                    {
                        await app.StopAsync(CancellationToken.None).ConfigureAwait(true);
                    }
                    finally
                    {
                        await app.DisposeAsync().ConfigureAwait(true);
                        DeleteTestDirectory(directory);
                    }
                }
            }
        });
    }

    [AvaloniaFact]
    public Task DelayedSoloParseCannotOverwriteRoomMediaAfterJoin()
    {
        return AvaloniaTestDispatcher.RunAsync(async () =>
        {
            var directory = CreateTestDirectory();
            var delayedParseStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseDelayedParse = new TaskCompletionSource<IInfoService?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var parser = new VideoParseCoordinator((input, _) =>
            {
                if (input.Contains(DelayedBvid, StringComparison.Ordinal))
                {
                    delayedParseStarted.TrySetResult();
                    // Deliberately ignore cancellation until released; production must
                    // still reject this stale completion before it commits its page.
                    return releaseDelayedParse.Task;
                }

                if (input.Contains(OldBvid, StringComparison.Ordinal))
                    return Task.FromResult<IInfoService?>(DetailService("Old title", Page(OldBvid, 1101, 1, "Old page")));
                if (input.Contains(RoomBvid, StringComparison.Ordinal))
                    return Task.FromResult<IInfoService?>(DetailService("Room title", Page(RoomBvid, 2202, 2202, "Room page")));
                return Task.FromResult<IInfoService?>(null);
            });

            var harness = CreateHarness(directory, parser);
            var app = CreateRoomServer();
            Task? delayedCommand = null;
            try
            {
                await app.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
                var address = WebSocketAddress(app);
                var hostClient = new WatchRoomClient();
                await using var hostLifetime = hostClient.ConfigureAwait(true);
                await hostClient.ConnectAsync(address, create: true, roomCode: null,
                    TestContext.Current.CancellationToken).ConfigureAwait(true);
                await hostClient.SendAsync(new
                {
                    type = "select",
                    media = new { bvid = RoomBvid, aid = 2202, cid = 2202 }
                }, TestContext.Current.CancellationToken).ConfigureAwait(true);

                var viewModel = harness.ViewModel;
                await viewModel.InitializeAsync().ConfigureAwait(true);
                viewModel.RoomVideoInput = $"https://www.bilibili.com/video/{OldBvid}";
                await viewModel.ParseCommand.ExecuteAsync(null).ConfigureAwait(true);
                Assert.Equal("Old title · Old page", viewModel.RoomFilmTitle);

                viewModel.RoomVideoInput = $"https://www.bilibili.com/video/{DelayedBvid}";
                delayedCommand = viewModel.ParseCommand.ExecuteAsync(null);
                await delayedParseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5),
                    TestContext.Current.CancellationToken).ConfigureAwait(true);

                viewModel.ServiceAddress = address;
                viewModel.InviteText = hostClient.RoomCode!;
                await viewModel.JoinRoomCommand.ExecuteAsync(null).ConfigureAwait(true);
                Assert.Equal("Room title · Room page", viewModel.RoomFilmTitle);

                releaseDelayedParse.TrySetResult(
                    DetailService("Delayed title", Page(DelayedBvid, 3303, 3, "Delayed page")));
                await delayedCommand.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)
                    .ConfigureAwait(true);

                Assert.Equal("Room title · Room page", viewModel.RoomFilmTitle);
                Assert.Equal($"https://www.bilibili.com/video/{RoomBvid}", viewModel.RoomVideoInput);
                Assert.False(viewModel.PlayerReady);
            }
            finally
            {
                releaseDelayedParse.TrySetResult(
                    DetailService("Delayed title", Page(DelayedBvid, 3303, 3, "Delayed page")));
                try
                {
                    if (delayedCommand is not null)
                        await delayedCommand.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
                }
                finally
                {
                    try
                    {
                        await harness.DisposeAsync().ConfigureAwait(true);
                    }
                    finally
                    {
                        try
                        {
                            await app.StopAsync(CancellationToken.None).ConfigureAwait(true);
                        }
                        finally
                        {
                            await app.DisposeAsync().ConfigureAwait(true);
                            DeleteTestDirectory(directory);
                        }
                    }
                }
            }
        });
    }

    private static WatchHarness CreateHarness(string directory, VideoParseCoordinator parser)
    {
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
            services.Replace(ServiceDescriptor.Singleton(
                new SqliteDownloadTaskStoreOptions(databasePath)));
            services.Replace(ServiceDescriptor.Singleton<IUserSessionCoordinator>(new LoggedInTestSession()));
            services.Replace(ServiceDescriptor.Singleton(parser));
        });
        return new WatchHarness(databasePath, settings, logProvider, loggerFactory, host,
            host.Services.GetRequiredService<WatchWindowViewModel>());
    }

    private static TestInfoService DetailService(string title, params VideoPage[] pages) =>
        new TestInfoService(title, [new VideoSection { VideoPages = pages }]);

    private static VideoPage[] Page(string bvid, long aid, long cid, string name) =>
        [new VideoPage { Bvid = bvid, Avid = aid, Cid = cid, Page = 1, Name = name }];

    private static WebApplication CreateRoomServer() => RoomServerHost.Build([
        "--RoomServer:ListenUrl", "http://127.0.0.1:0",
        "--Logging:LogLevel:Default", "None"]);

    private static string WebSocketAddress(WebApplication app) => new UriBuilder(app.Urls.Single())
    {
        Scheme = "ws",
        Path = "/ws"
    }.Uri.AbsoluteUri;

    private static string CreateTestDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bilicinema-room-media-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTestDirectory(string directory)
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private sealed class WatchHarness(
        string databasePath,
        SettingsStore settings,
        ApplicationLogProvider logProvider,
        ILoggerFactory loggerFactory,
        IHost host,
        WatchWindowViewModel viewModel) : IAsyncDisposable
    {
        public WatchWindowViewModel ViewModel { get; } = viewModel;

        public async ValueTask DisposeAsync()
        {
            await ((IAsyncDisposable)host).DisposeAsync().ConfigureAwait(true);
            loggerFactory.Dispose();
            await logProvider.DisposeAsync().ConfigureAwait(true);
            await settings.DisposeAsync().ConfigureAwait(true);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = true,
                DefaultTimeout = 5
            }.ToString());
            SqliteConnection.ClearPool(connection);
        }
    }

    private sealed class LoggedInTestSession : IUserSessionCoordinator
    {
        public Task<UserSessionSnapshot> RefreshAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new UserSessionSnapshot(
                new UserInfoForNavigation { IsLogin = true, Mid = 1, Name = "test" },
                HasLoginFile: false));
        }
    }

    private sealed class TestInfoService(string title, IList<VideoSection> sections) : IInfoService
    {
        public VideoInfoView? GetVideoView(CancellationToken cancellationToken = default) =>
            new() { Title = title };

        public IList<VideoSection>? GetVideoSections(bool noUgc, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return sections;
        }

        public IList<VideoPage>? GetVideoPages(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return sections.SelectMany(section => section.VideoPages).ToArray();
        }

        public Task<PlayUrl?> GetVideoStreamAsync(VideoPage page, CancellationToken cancellationToken = default) =>
            Task.FromResult<PlayUrl?>(null);
    }
}
