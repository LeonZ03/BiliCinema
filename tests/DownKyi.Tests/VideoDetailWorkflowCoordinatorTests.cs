using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Presentation;
using DownKyi.Services;
using DownKyi.Services.Video;

namespace DownKyi.Tests;

public sealed class VideoDetailWorkflowCoordinatorTests
{
    [Fact]
    public void StartingNewOperationCancelsPreviousGeneration()
    {
        using var settings = new TestSettingsStore();
        var client = new TestBilibiliApiClient();
        using var coordinator = new VideoDetailWorkflowCoordinator(
            settings.Store,
            new VideoTagProvider(client),
            new TestWbiKeyProvider(),
            client);

        var first = coordinator.StartOperation();
        var second = coordinator.StartOperation();

        Assert.True(first.CancellationToken.IsCancellationRequested);
        Assert.False(second.CancellationToken.IsCancellationRequested);
        Assert.False(coordinator.IsCurrent(first));
        Assert.True(coordinator.IsCurrent(second));
    }

    [Fact]
    public void SetInputNormalizesShareTextBeforeCaching()
    {
        var parseCoordinator = new VideoParseCoordinator((_, _) => new StubInfoService());
        using var coordinator = new VideoDetailWorkflowCoordinator(parseCoordinator, new VideoSearchState());

        var normalized = coordinator.SetInput("【猫鼠游戏】 https://www.bilibili.com/bangumi/play/ep251429/?share_source=copy_web&");

        Assert.Equal("https://www.bilibili.com/bangumi/play/ep251429/?share_source=copy_web&", normalized);
        Assert.Equal(normalized, coordinator.CurrentInput);
    }

    [Fact]
    public async Task DetailLoadAndSearchReuseOriginalPageObjects()
    {
        var alpha = new VideoPage { Cid = 1, Name = "Alpha" };
        var beta = new VideoPage { Cid = 2, Name = "Beta" };
        var section = new VideoSection
        {
            Id = 1,
            VideoPages = [alpha, beta]
        };
        var parseCoordinator = new VideoParseCoordinator((_, _) => new StubInfoService
        {
            VideoInfo = new VideoInfoView(),
            VideoSections = [section]
        });
        using var coordinator = new VideoDetailWorkflowCoordinator(parseCoordinator, new VideoSearchState());
        var operation = coordinator.StartOperation();
        coordinator.Reset();
        coordinator.SetInput("BV17x411w7KC");

        var result = await coordinator.LoadDetailAsync(operation);
        coordinator.ApplySearch("Beta");

        Assert.Same(section, Assert.Single(result.VideoSections));
        Assert.Same(beta, Assert.Single(section.VideoPages));
    }

    private sealed class StubInfoService : IInfoService
    {
        public VideoInfoView? VideoInfo { get; init; }

        public IList<VideoSection>? VideoSections { get; init; }

        public VideoInfoView? GetVideoView(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return VideoInfo;
        }

        public IList<VideoSection>? GetVideoSections(
            bool noUgc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return VideoSections;
        }

        public IList<VideoPage>? GetVideoPages(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<PlayUrl?> GetVideoStreamAsync(
            VideoPage page,
            CancellationToken cancellationToken = default)
        {
            return Task.FromException<PlayUrl?>(new NotSupportedException());
        }
    }
}
