using System.Text;
using DownKyi.Core.BiliApi.Zone;
using DownKyi.Core.Danmaku2Ass;

namespace DownKyi.Core.Tests;

public sealed class DanmakuAndZoneContractTests
{
    [Fact]
    public void ZoneImageLookupUsesKnownAndFallbackKeys()
    {
        Assert.Equal("Zone.techDrawingImage", VideoZoneIcon.GetZoneImageKey(36));
        Assert.Equal("videoUpDrawingImage", VideoZoneIcon.GetZoneImageKey(int.MaxValue));
    }

    [Fact]
    public void StudioUsesInjectedOutputEncoding()
    {
        var path = Path.Combine(Path.GetTempPath(), $"downkyi-studio-{Guid.NewGuid():N}.txt");
        var encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        var studio = new Studio(new Config(), new List<Danmaku>(), encoding);

        try
        {
            studio.CreateFile(path, "字幕");

            var bytes = File.ReadAllBytes(path);
            Assert.True(bytes.AsSpan().StartsWith(encoding.GetPreamble()));
            Assert.Equal("字幕", File.ReadAllText(path, encoding));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void StudioDoesNotHideOutputWriteFailures()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"downkyi-studio-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var studio = new Studio(new Config(), [], Encoding.UTF8);

        try
        {
            var exception = Record.Exception(() => studio.CreateFile(directory, "subtitle"));

            Assert.True(
                exception is IOException or UnauthorizedAccessException,
                $"Expected a visible file-system failure, got {exception?.GetType().Name ?? "no exception"}.");
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void ProducerReportReturnsSummaryAndPerFilterCounts()
    {
        var producer = new Producer(
            new Dictionary<string, bool>
            {
                ["top_filter"] = false,
                ["bottom_filter"] = false,
                ["scroll_filter"] = false
            },
            []);

        producer.StartHandle();
        var report = producer.Report();

        Assert.Equal(0, report["blocked"]);
        Assert.Equal(0, report["passed"]);
        Assert.Equal(0, report["total"]);
        Assert.Equal(0, report["top_filter"]);
        Assert.Equal(0, report["bottom_filter"]);
        Assert.Equal(0, report["scroll_filter"]);
    }

    [Fact]
    public void CreatorUsesCollisionFreeTrackBeforeAllowingOverlap()
    {
        var creator = new Creater(
            CreateDanmakuConfig(lineCount: 2),
            [
                CreateTopDanmaku(start: 0, content: new string('A', 24)),
                CreateTopDanmaku(start: 1, content: "B")
            ]);

        Assert.Equal(2, creator.Subtitles.Count);
        Assert.Equal(0, creator.Subtitles[0].Display.LineIndex);
        Assert.Equal(1, creator.Subtitles[1].Display.LineIndex);
        Assert.Equal(1, creator.Subtitles[1].Start);
    }

    [Fact]
    public void CreatorKeepsDanmakuOnLeastOverlappingTrackWhenAllTracksAreBusy()
    {
        var creator = new Creater(
            CreateDanmakuConfig(lineCount: 2),
            [
                CreateTopDanmaku(start: 0, content: new string('A', 24)),
                CreateTopDanmaku(start: 0, content: "B"),
                CreateTopDanmaku(start: 1, content: "C")
            ]);

        Assert.Equal(3, creator.Subtitles.Count);
        Assert.Equal(1, creator.Subtitles[2].Display.LineIndex);
        Assert.Equal(1, creator.Subtitles[2].Start);
    }

    [Fact]
    public void CollisionKeepsLatestLeaveTimeAfterOverlappingPlacement()
    {
        var config = CreateDanmakuConfig(lineCount: 1);
        var collision = new Collision(config.LineCount);
        var longDisplay = new TopDisplay(config, CreateTopDanmaku(start: 0, content: new string('A', 24)));
        var overlappingDisplay = new TopDisplay(config, CreateTopDanmaku(start: 1, content: "B"));

        collision.Update(longDisplay.Leave, lineIndex: 0, offset: 0);
        collision.Update(overlappingDisplay.Leave, lineIndex: 0, offset: 0);

        var (_, overlap) = collision.Detect(
            new TopDisplay(config, CreateTopDanmaku(start: 5, content: "C")));

        Assert.Equal(1, overlap);
    }

    private static Config CreateDanmakuConfig(int lineCount)
    {
        return new Config
        {
            BaseFontSize = 50,
            LineCount = lineCount,
            DropOffset = 0
        };
    }

    private static Danmaku CreateTopDanmaku(float start, string content)
    {
        return new Danmaku
        {
            Start = start,
            Style = "top",
            Color = 0xFFFFFF,
            Content = content,
            SizeRatio = 1
        };
    }
}
