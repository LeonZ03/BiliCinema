namespace DownKyi.Models;

internal class Downloaded
{
    public string Id { get; set; } = null!;

    //  下载速度
    public string? MaxSpeedDisplay { get; set; }

    // 完成时间戳
    public long FinishedTimestamp { get; set; }

    // 完成时间
    public string FinishedTime { get; set; } = string.Empty;

    public DownloadBase? DownloadBase { get; set; }
}
