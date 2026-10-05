using DownKyi.Presentation;
using DownKyi.Services.Watch;
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
}
