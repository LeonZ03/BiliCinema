using System;
using System.IO;
using System.Security.Cryptography;

namespace DownKyi.Services.Watch;

internal static class BundledTools
{
    private static string ToolRoot => Environment.GetEnvironmentVariable("BILICINEMA_TOOL_ROOT")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BiliCinema", "Tools");

    public static string? EnsureTunnelTool() => Extract("cloudflared.exe", "cloudflared.exe");

    public static void EnsureDownloadTools()
    {
        var aria = Extract("aria2c.exe", Path.Combine("aria2", "aria2c.exe"));
        var ffmpeg = Extract("ffmpeg.exe", Path.Combine("ffmpeg", "ffmpeg.exe"));
        var ffprobe = Extract("ffprobe.exe", Path.Combine("ffmpeg", "ffprobe.exe"));
        Extract("ffmpeg-LICENSE.txt", Path.Combine("ffmpeg", "LICENSE.txt"));
        if (aria == null || ffmpeg == null || ffprobe == null)
        {
            // Source builds still use the original external asset scripts and paths.
            return;
        }

        Environment.SetEnvironmentVariable("BILICINEMA_TOOL_ROOT", ToolRoot);
    }

    private static string? Extract(string resourceFileName, string relativePath)
    {
        var assembly = typeof(BundledTools).Assembly;
        var resourceName = "BiliCinema.Tools." + resourceFileName;
        using var resource = assembly.GetManifestResourceStream(resourceName);
        using var checksumResource = assembly.GetManifestResourceStream(resourceName + ".sha256");
        if (resource == null || checksumResource == null)
        {
            return null;
        }

        using var checksumReader = new StreamReader(checksumResource);
        var expected = checksumReader.ReadToEnd().Trim();
        if (expected.Length != 64)
        {
            throw new InvalidDataException("Embedded tool checksum is invalid.");
        }

        var destination = Path.Combine(ToolRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (!Matches(destination, expected))
        {
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = File.Create(temporary))
                {
                    resource.CopyTo(output);
                }

                if (!Matches(temporary, expected))
                {
                    throw new InvalidDataException("Embedded tool failed its checksum check.");
                }

                File.Move(temporary, destination, true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }

        if (resourceFileName == "aria2c.exe")
        {
            File.WriteAllText(destination + ".sha256", expected);
        }

        return destination;
    }

    private static bool Matches(string path, string expected)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        using var file = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(file));
        return actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
}
