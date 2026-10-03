using DownKyi.Application.Downloads;
using DownKyi.Images;
using DownKyi.Models;

namespace DownKyi.ViewModels.DownloadManager;

internal class DownloadedItem : DownloadBaseItem
{
    // model数据
    public Downloaded Downloaded { get; set; } = null!;

    public DownloadHistoryRecord HistoryRecord { get; set; } = null!;

    //  下载速度
    public string? MaxSpeedDisplay => Downloaded.MaxSpeedDisplay;

    // 完成时间
    public string FinishedTime => Downloaded.FinishedTime;

    #region 控制按钮

    public VectorImage OpenFolder { get; } = ButtonIcon.Folder;

    public VectorImage OpenVideo { get; } = ButtonIcon.Start;

    public VectorImage RemoveVideo { get; } = ButtonIcon.Trash;

    #endregion
}
