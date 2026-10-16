using DownKyi.Services.Watch;
using DownKyi.ViewModels;

namespace DownKyi.Desktop.Tests;

public sealed class RoomPlaybackCorrectionTests
{
    [Fact]
    public void UnavailableGuestWaitsLocallyAndRecoveryAlignsOnceToCurrentHost()
    {
        var snapshot = new WatchRoomSnapshot
        {
            Playing = true,
            Host = new WatchRoomMember { Online = true, Ready = true },
            Guests = [new WatchRoomMember { Online = true, Ready = false, Buffering = true }]
        };
        var waiting = WatchWindowViewModel.CreatePlaybackCorrection(false, snapshot, drift: 20,
            currentRate: 1, forceSync: true, localAvailable: false);
        Assert.True(waiting.ShouldPause);
        Assert.False(waiting.Seek);
        var recovered = WatchWindowViewModel.CreatePlaybackCorrection(false, snapshot, drift: 0.2,
            currentRate: 1, forceSync: true);
        Assert.True(recovered.Seek);
        Assert.False(recovered.ShouldPause);
        var following = WatchWindowViewModel.CreatePlaybackCorrection(false, snapshot, drift: 0.2,
            currentRate: 1);
        Assert.False(following.Seek);
        Assert.False(following.ShouldPause);
        Assert.False(WatchWindowViewModel.CreatePlaybackCorrection(true, snapshot, 0, 1).ShouldPause);
    }

    [Fact]
    public void PhysicalPauseWhileWaitingForHostBufferingPreservesHostIntent()
    {
        var snapshot = new WatchRoomSnapshot
        {
            WaitingForReady = true,
            Host = new WatchRoomMember { Online = true, Ready = true, Buffering = true }
        };
        Assert.True(WatchWindowViewModel.HostPlaybackIntent(false, false, snapshot));
        Assert.False(WatchWindowViewModel.HostPlaybackIntent(false, false,
            snapshot with { WaitingForReady = false }));
        Assert.True(WatchWindowViewModel.HostPlaybackIntent(true, false, null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitSynchronizationAlignsEvenSmallDriftAndRestoresHostRate(bool playing)
    {
        var snapshot = new WatchRoomSnapshot
        {
            Playing = playing,
            Rate = 1,
            Host = new WatchRoomMember { Online = true, Ready = true }
        };

        var correction = WatchWindowViewModel.CreatePlaybackCorrection(
            isHost: false, snapshot, drift: 0.2, currentRate: 1.04, forceSync: true);

        Assert.True(correction.Seek);
        Assert.True(correction.SetRate);
        Assert.Equal(1, correction.TargetRate);
        Assert.Equal(!playing, correction.ShouldPause);
        var host = WatchWindowViewModel.CreatePlaybackCorrection(
            isHost: true, snapshot, drift: 8, currentRate: 1, forceSync: true);
        Assert.False(host.Seek);
        Assert.False(host.SetRate);
    }

    [Fact]
    public void HostPlaybackRateAndPositionRemainAuthoritative()
    {
        var snapshot = new WatchRoomSnapshot
        {
            Version = 12,
            Media = new WatchRoomMedia { EpisodeId = 123 },
            Playing = true,
            Rate = 1,
            Host = new WatchRoomMember { Online = true, Ready = true }
        };

        var correction = WatchWindowViewModel.CreatePlaybackCorrection(
            isHost: true, snapshot, drift: 8, currentRate: 3);

        Assert.False(correction.Seek);
        Assert.False(correction.SetRate);
        Assert.Equal(1, correction.TargetRate);
        Assert.False(correction.ShouldPause);
    }

    [Fact]
    public void GuestStillReceivesSeekAndSmallDriftRateCorrection()
    {
        var snapshot = new WatchRoomSnapshot
        {
            Version = 12,
            Media = new WatchRoomMedia { EpisodeId = 123 },
            Playing = true,
            Rate = 1,
            Host = new WatchRoomMember { Online = true, Ready = true }
        };

        var seekCorrection = WatchWindowViewModel.CreatePlaybackCorrection(
            isHost: false, snapshot, drift: 8, currentRate: 1);
        var rateCorrection = WatchWindowViewModel.CreatePlaybackCorrection(
            isHost: false, snapshot, drift: 1, currentRate: 1);

        Assert.True(seekCorrection.Seek);
        Assert.True(rateCorrection.SetRate);
        Assert.Equal(1.04, rateCorrection.TargetRate, 3);
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("", null, true)]
    [InlineData("ws://127.0.0.1:5077/ws", null, true)]
    [InlineData("wss://watch.example/ws", "wss://quick-tunnel.example/ws", false)]
    public void BlankAddressUsesAutomaticTunnelButManualRemoteAddressIsPreserved(
        string? address, string? tunnelAddress, bool expected)
    {
        Assert.Equal(expected,
            WatchWindowViewModel.UsesAutomaticRoomAddress(address, tunnelAddress));
    }
}
