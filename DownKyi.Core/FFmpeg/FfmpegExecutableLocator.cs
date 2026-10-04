using System.Runtime.InteropServices;

namespace DownKyi.Core.FFmpeg;

internal static class FfmpegExecutableLocator
{
    public static string Ffmpeg => Locate("ffmpeg");

    public static string Ffprobe => Locate("ffprobe");

    private static string Locate(string name)
    {
        var fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? $"{name}.exe"
            : name;
        var toolRoot = Environment.GetEnvironmentVariable("BILICINEMA_TOOL_ROOT");
        if (!string.IsNullOrWhiteSpace(toolRoot))
        {
            var extractedPath = Path.Combine(toolRoot, "ffmpeg", fileName);
            if (File.Exists(extractedPath))
            {
                return extractedPath;
            }
        }
        var bundledPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg", fileName);
        return File.Exists(bundledPath) ? bundledPath : name;
    }
}
