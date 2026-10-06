using DownKyi.Presentation;
using DownKyi.Services.Watch;
using DownKyi.ViewModels;
using DownKyi.Views;

namespace DownKyi.Desktop.Tests;

public sealed class BilibiliWebPlaybackBootstrapTests
{
    [Fact]
    public void BangumiPlayerUrlIsAllowedAndUsesEpisodePage()
    {
        var url = BilibiliWebPlaybackSession.BuildPlayerUri(new VideoPage { EpisodeId = 818558 });

        Assert.Equal("https://www.bilibili.com/bangumi/play/ep818558", url.AbsoluteUri);
        Assert.True(WatchWindow.IsAllowedPlaybackNavigation(url));
    }

    [Fact]
    public void OrdinaryVideoPlayerUrlIsAllowedAndKeepsSelectedPage()
    {
        var url = BilibiliWebPlaybackSession.BuildPlayerUri(new VideoPage
        {
            Bvid = "BV1ZpYd66ELP",
            Avid = 123,
            Page = 2
        });

        Assert.Equal("https://www.bilibili.com/video/BV1ZpYd66ELP?p=2", url.AbsoluteUri);
        Assert.True(WatchWindow.IsAllowedPlaybackNavigation(url));
    }

    [Theory]
    [InlineData("https://www.bilibili.com/video/BV1ZpYd66ELP")]
    [InlineData("https://www.bilibili.com/bangumi/play/ep818558")]
    public void PlaybackPathsStayInAllowList(string address)
    {
        Assert.True(WatchWindow.IsAllowedPlaybackNavigation(new Uri(address)));
    }

    [Fact]
    public void NonPlaybackPagesStayBlocked()
    {
        Assert.False(WatchWindow.IsAllowedPlaybackNavigation(new Uri("https://www.bilibili.com/")));
        Assert.False(WatchWindow.IsAllowedPlaybackNavigation(new Uri("https://www.bilibili.com/video/not-a-bvid")));
        Assert.False(WatchWindow.IsAllowedPlaybackNavigation(new Uri("https://example.com/video/BV1ZpYd66ELP")));
    }

    [Fact]
    public void PlayerEpisodeUrlSelectsTheChangedEpisode()
    {
        var pages = new[]
        {
            new VideoPage { EpisodeId = 101, Name = "第1集" },
            new VideoPage { EpisodeId = 102, Name = "第2集" }
        };

        var selected = WatchWindowViewModel.SelectPlayerPage(pages,
            new Uri("https://www.bilibili.com/bangumi/play/ep102"));

        Assert.Same(pages[1], selected);
    }

    [Fact]
    public void PlayerPartUrlSelectsTheChangedPartWithoutChangingBvid()
    {
        var pages = new[]
        {
            new VideoPage { Bvid = "BV1ZpYd66ELP", Cid = 201, Page = 1 },
            new VideoPage { Bvid = "BV1ZpYd66ELP", Cid = 202, Page = 2 }
        };

        var selected = WatchWindowViewModel.SelectPlayerPage(pages,
            new Uri("https://www.bilibili.com/video/BV1ZpYd66ELP?p=2"));

        Assert.Same(pages[1], selected);
        Assert.Null(WatchWindowViewModel.SelectPlayerPage(pages,
            new Uri("https://example.com/video/BV1ZpYd66ELP?p=2")));
    }

    [Fact]
    public void PlayerCidSelectsNewEpisodeWhenSiteKeepsTheOldUrl()
    {
        var pages = new[]
        {
            new VideoPage { EpisodeId = 101, Cid = 201 },
            new VideoPage { EpisodeId = 102, Cid = 202 }
        };

        var selected = WatchWindowViewModel.SelectPlayerPage(pages,
            new Uri("https://www.bilibili.com/bangumi/play/ep101"),
            cid: 202, current: pages[0]);

        Assert.Same(pages[1], selected);
    }
}
