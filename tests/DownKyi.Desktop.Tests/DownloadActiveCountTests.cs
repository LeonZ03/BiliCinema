using Avalonia.Headless.XUnit;
using DownKyi.Models;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Desktop.Tests;

public sealed class DownloadActiveCountTests
{
    [AvaloniaFact]
    public async Task BadgeCountsOnlyActiveTasksAndTracksStatusChanges()
    {
        await AvaloniaTestDispatcher.RunAsync(() =>
        {
            DesktopTestResources.EnsureDownloadProjectionResources();
            var lists = new DownloadListState();
            var active = new DownloadingItem
            {
                Downloading = new Downloading { DownloadStatus = DownloadStatus.Downloading }
            };
            var queued = new DownloadingItem
            {
                Downloading = new Downloading { DownloadStatus = DownloadStatus.WaitForDownload }
            };

            lists.AddDownloadingRange([active, queued]);
            Assert.Equal(1, lists.ActiveDownloadingCount);

            queued.Downloading = new Downloading { DownloadStatus = DownloadStatus.Downloading };
            Assert.Equal(2, lists.ActiveDownloadingCount);

            active.Downloading = new Downloading { DownloadStatus = DownloadStatus.Pause };
            Assert.Equal(1, lists.ActiveDownloadingCount);

            Assert.True(lists.RemoveDownloading(queued));
            Assert.Equal(0, lists.ActiveDownloadingCount);
        }).ConfigureAwait(true);
    }
}
