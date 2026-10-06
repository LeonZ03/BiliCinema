using System.Text.Json;
using DownKyi.Services.Watch;

namespace DownKyi.Desktop.Tests;

public sealed class RoomActionOverlayTests
{
    [Fact]
    public void ConsecutiveHostActionsRemainSeparateWithIndividualExpiry()
    {
        var script = RoomChatOverlayScript.Build(
            [], null, null, fullscreen: true, inRoom: true, memberCount: 2,
            revision: 0, toastSequence: 0, toastRemainingMilliseconds: 0,
            hostActionNotices: [
                new WatchRoomActionNotice(11, "房主暂停了播放", 300),
                new WatchRoomActionNotice(12, "房主跳转到 00:20:00", 750),
                new WatchRoomActionNotice(13, "房主开始播放", 1000)
            ], hostActionToastSequence: 13);

        const string marker = "const state = ";
        var start = script.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = script.IndexOf(';', start);
        using var state = JsonDocument.Parse(script[start..end]);
        var notices = state.RootElement.GetProperty("hostActionNotices").EnumerateArray().ToArray();

        Assert.Equal(3, notices.Length);
        Assert.Equal("11,12,13", string.Join(',', notices.Select(notice =>
            notice.GetProperty("id").GetInt32())));
        Assert.Equal("300,750,1000", string.Join(',', notices.Select(notice =>
            notice.GetProperty("remainingMilliseconds").GetInt32())));
        Assert.Contains("ui.actionToasts.appendChild(row)", script, StringComparison.Ordinal);
    }
}
