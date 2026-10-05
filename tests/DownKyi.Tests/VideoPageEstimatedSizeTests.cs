using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Core.Utils;
using DownKyi.Presentation;

namespace DownKyi.Tests;

public sealed class VideoPageEstimatedSizeTests
{
    [Fact]
    public void DashEstimateUsesSelectedVideoAudioAndDuration()
    {
        var page = CreatePage();

        Assert.Equal("约 " + Format.FormatFileSize(12_500), page.EstimatedSizeText);

        page.AudioQualityFormat = "中质量";

        Assert.Equal("约 " + Format.FormatFileSize(15_000), page.EstimatedSizeText);
    }

    [Fact]
    public void ExactDurlSizeIsShownWithoutApproximation()
    {
        var page = CreatePage();
        page.PlayUrl = new PlayUrl
        {
            Durl = [new PlayUrlDurl { Size = 24_576 }]
        };

        Assert.Equal(Format.FormatFileSize(24_576), page.EstimatedSizeText);
    }

    private static VideoPage CreatePage() => new()
    {
        AudioQualityFormat = "低质量",
        VideoQuality = new VideoQuality
        {
            Quality = 64,
            SelectedVideoCodec = "H.264/AVC"
        },
        PlayUrl = new PlayUrl
        {
            Dash = new PlayUrlDash
            {
                Duration = 10,
                Video = [new PlayUrlDashVideo { Id = 64, CodecId = 7, Bandwidth = 8_000 }],
                Audio =
                [
                    new PlayUrlDashVideo { Id = 30216, Bandwidth = 2_000 },
                    new PlayUrlDashVideo { Id = 30232, Bandwidth = 4_000 }
                ]
            }
        }
    };
}
