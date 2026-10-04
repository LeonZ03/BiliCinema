using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Presentation;
using DownKyi.Services.Watch;

namespace DownKyi.Desktop.Tests;

public sealed class PlaybackTrackSelectorTests
{
    [Fact]
    public void Selected4kRequiresExactVideoAndAnAudioTrack()
    {
        var page = CreatePage(
            video: new PlayUrlDashVideo
            {
                Id = 120,
                CodecId = 12,
                Width = 3840,
                Height = 2160,
                Codecs = "hev1",
                BaseAddress = "https://upos.example.bilivideo.com/video.m4s?token=local"
            },
            audio: new PlayUrlDashVideo
            {
                Id = 30280,
                Codecs = "mp4a.40.2",
                BaseAddress = "https://upos.example.bilivideo.com/audio.m4s?token=local"
            });

        var selected = PlaybackTrackSelector.Select(page, 120, 12, 30280);

        Assert.Equal(3840, selected.Width);
        Assert.Equal(2160, selected.Height);
        Assert.Equal("mp4a.40.2", selected.AudioCodec);
        Assert.Throws<InvalidOperationException>(() => PlaybackTrackSelector.Select(page, 120, 7, 30280));
    }

    [Fact]
    public void PlaybackDoesNotSilentlyFallBackOrAcceptForeignHosts()
    {
        var page = CreatePage(
            video: new PlayUrlDashVideo
            {
                Id = 80,
                CodecId = 12,
                BaseAddress = "https://upos.example.bilivideo.com/video.m4s"
            },
            audio: new PlayUrlDashVideo
            {
                Id = 30280,
                BaseAddress = "https://media.evil.example/audio.m4s"
            });

        Assert.Throws<InvalidOperationException>(() => PlaybackTrackSelector.Select(page, 120, 12, 30280));
        Assert.Throws<InvalidOperationException>(() => PlaybackTrackSelector.Select(page, 80, 12, 30280));
    }

    [Fact]
    public void OnlinePlaybackPrefersSeekableMediumAudioWhileExplicitSelectionStaysHigh()
    {
        var page = CreatePage(
            video: new PlayUrlDashVideo
            {
                Id = 120,
                CodecId = 12,
                BaseAddress = "https://upos.example.bilivideo.com/video.m4s"
            },
            audio: new PlayUrlDashVideo
            {
                Id = 30280,
                BaseAddress = "https://upos.example.bilivideo.com/high.m4s"
            });
        page.PlayUrl!.Dash.Audio =
        [
            page.PlayUrl.Dash.Audio[0],
            new PlayUrlDashVideo
            {
                Id = 30232,
                BaseAddress = "https://upos.example.bilivideo.com/medium.m4s"
            }
        ];
        page.VideoQuality = new VideoQuality { Quality = 120, SelectedVideoCodec = "H.265/HEVC" };
        page.AudioQualityFormat = "高质量";

        var online = PlaybackTrackSelector.SelectForOnlinePlayback(page);
        var explicitlyHigh = PlaybackTrackSelector.Select(page, 120, 12, 30280);

        Assert.Equal(30232, online.AudioQuality);
        Assert.Equal(30280, explicitlyHigh.AudioQuality);
    }

    private static VideoPage CreatePage(PlayUrlDashVideo video, PlayUrlDashVideo audio)
    {
        return new VideoPage
        {
            PlayUrl = new PlayUrl
            {
                Dash = new PlayUrlDash
                {
                    Video = [video],
                    Audio = [audio]
                }
            }
        };
    }
}
