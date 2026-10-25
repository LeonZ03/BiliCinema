using DownKyi.Application.Media;

namespace DownKyi.Application.Tests;

public sealed class VideoInputResolverTests
{
    [Theory]
    [InlineData("BV17x411w7KC", VideoInputKind.Video)]
    [InlineData("av170001", VideoInputKind.Video)]
    [InlineData("https://www.bilibili.com/video/BV17x411w7KC", VideoInputKind.Video)]
    [InlineData("ss32982", VideoInputKind.Bangumi)]
    [InlineData("ep317925", VideoInputKind.Bangumi)]
    [InlineData("md28228367", VideoInputKind.Bangumi)]
    [InlineData("https://www.bilibili.com/bangumi/play/ss32982", VideoInputKind.Bangumi)]
    [InlineData("https://www.bilibili.com/cheese/play/ep3489", VideoInputKind.Cheese)]
    [InlineData("https://b23.tv/ep251429", VideoInputKind.Bangumi)]
    [InlineData("https://b23.tv/ss32982", VideoInputKind.Bangumi)]
    [InlineData("https://www.bilibili.com/bangumi/media/md28228367", VideoInputKind.Bangumi)]
    [InlineData("https://evil.example/video/BV17x411w7KC", VideoInputKind.Unknown)]
    [InlineData("https://user@www.bilibili.com/video/BV17x411w7KC", VideoInputKind.Unknown)]
    [InlineData("ftp://www.bilibili.com/video/BV17x411w7KC", VideoInputKind.Unknown)]
    [InlineData("not-a-video", VideoInputKind.Unknown)]
    public void ResolveReturnsExpectedInputKind(string input, VideoInputKind expectedKind)
    {
        Assert.Equal(expectedKind, VideoInputResolver.Resolve(input));
    }

    [Theory]
    [InlineData("【猫鼠游戏】 https://www.bilibili.com/bangumi/play/ep251429/?share_source=copy_web&", "https://www.bilibili.com/bangumi/play/ep251429/?share_source=copy_web&")]
    [InlineData("电影分享：\nhttps://www.bilibili.com/video/BV17x411w7KC/?p=3。\n来自哔哩哔哩", "https://www.bilibili.com/video/BV17x411w7KC/?p=3")]
    [InlineData("https://www.bilibili.com/video/BV17x411w7KC?p=3，链接，复制打开观看", "https://www.bilibili.com/video/BV17x411w7KC?p=3")]
    [InlineData("[分享](https\\://www\\.bilibili\\.com/video/BV17x411w7KC?p=2)", "https://www.bilibili.com/video/BV17x411w7KC?p=2")]
    [InlineData("\n  ep251429  \n", "ep251429")]
    public void NormalizeExtractsTrustedVideoInput(string input, string expected)
    {
        Assert.Equal(expected, VideoInputResolver.Normalize(input));
    }

    [Fact]
    public void NormalizeDoesNotExtractAnIdentifierFromAnUntrustedUrlOrTitle()
    {
        const string input = "BV17x411w7KC https://evil.example/video/av170001";

        Assert.Equal(input, VideoInputResolver.Normalize(input));
        Assert.Equal(VideoInputKind.Unknown, VideoInputResolver.Resolve(input));
    }

    [Theory]
    [InlineData("【标题 BV17x411w7KC】 https://evil.example/video/av170001")]
    [InlineData("【标题 ep251429】 https://user@www.bilibili.com/bangumi/play/ep317925")]
    [InlineData("【标题 BV17x411w7KC】 ftp://www.bilibili.com/video/av170001")]
    public void ResolveRejectsUnsafeUrlInsteadOfUsingTitleIdentifiers(string input)
    {
        Assert.Equal(VideoInputKind.Unknown, VideoInputResolver.Resolve(input));
    }
}
