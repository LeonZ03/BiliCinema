using DownKyi.Application.Downloads;
using DownKyi.Images;
using DownKyi.Models;

namespace DownKyi.ViewModels.DownloadManager;

internal class DownloadedItem : DownloadBaseItem
{
    public DownloadedItem()
    {
        // 打开文件夹按钮
        OpenFolder = ButtonIcon.Current.Folder;

        // 打开视频按钮
        OpenVideo = ButtonIcon.Current.Start;

        // 删除按钮
        RemoveVideo = ButtonIcon.Current.Trash;
    }

    // model数据
    public Downloaded Downloaded { get; set; } = null!;

    public DownloadHistoryRecord HistoryRecord { get; set; } = null!;

    //  下载速度
    public string? MaxSpeedDisplay => Downloaded.MaxSpeedDisplay;

    // 完成时间
    public string FinishedTime => Downloaded.FinishedTime;

    #region 控制按钮

    private VectorImage _openFolder = null!;

    public VectorImage OpenFolder
    {
        get => _openFolder;
        set => SetProperty(ref _openFolder, value);
    }

    private VectorImage _openVideo = null!;

    public VectorImage OpenVideo
    {
        get => _openVideo;
        set => SetProperty(ref _openVideo, value);
    }

    private VectorImage _removeVideo = null!;

    public VectorImage RemoveVideo
    {
        get => _removeVideo;
        set => SetProperty(ref _removeVideo, value);
    }

    #endregion
}
