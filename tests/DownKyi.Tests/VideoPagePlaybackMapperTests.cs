using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Core.Settings;
using DownKyi.Presentation;
using DownKyi.Services.Video;

namespace DownKyi.Tests;

public sealed class VideoPagePlaybackMapperTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "downkyi-video-page-playback-mapper-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ApplyPlayUrlFallsBackToAvailableCodecAtHighestQuality()
    {
        Directory.CreateDirectory(_directory);
        using var settingsStore = new SettingsStore(Path.Combine(_directory, "settings.json"));
        var settings = settingsStore.Current with
        {
            Video = settingsStore.Current.Video with
            {
                Quality = 127,
                VideoCodecs = 7
            },
            User = settingsStore.Current.User with
            {
                Mid = 1,
                IsLogin = true,
                IsVip = true
            }
        };
        var playUrl = new PlayUrl
        {
            Dash = new PlayUrlDash
            {
                Video =
                [
                    new PlayUrlDashVideo { Id = 126, CodecId = 12 },
                    new PlayUrlDashVideo { Id = 80, CodecId = 7 }
                ]
            },
            SupportFormats =
            [
                new PlayUrlSupportFormat { Quality = 126, NewDescription = "杜比视界" },
                new PlayUrlSupportFormat { Quality = 80, NewDescription = "1080P 高清" }
            ]
        };
        var page = new VideoPage();

        VideoPagePlaybackMapper.ApplyPlayUrl(playUrl, page, settings);

        Assert.Equal(126, page.VideoQuality.Quality);
        Assert.Equal("杜比视界", page.VideoQuality.QualityFormat);
        Assert.Equal("H.265/HEVC", page.VideoQuality.SelectedVideoCodec);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
