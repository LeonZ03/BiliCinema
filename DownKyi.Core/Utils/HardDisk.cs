namespace DownKyi.Core.Utils;

public static class HardDisk
{
    /// <summary>
    /// Gets the available free space of the drive containing <paramref name="path"/>.
    /// </summary>
    public static long GetHardDiskFreeSpace(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new DriveInfo(path).TotalFreeSpace;
    }
}
