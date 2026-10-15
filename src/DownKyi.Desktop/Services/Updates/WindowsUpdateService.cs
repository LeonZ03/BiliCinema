using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Diagnostics;
using DownKyi.Application.Lifetime;
using DownKyi.Models;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Updates;

internal sealed record UpdateProgress(string Message, double Percentage, bool CanCancel = true);

internal interface IWindowsUpdateService
{
    Task InstallAsync(
        GitHubRelease release,
        IProgress<UpdateProgress> progress,
        CancellationToken cancellationToken);
}

internal sealed partial class WindowsUpdateService(
    HttpClient httpClient,
    IApplicationLifecycle lifecycle,
    ILogger<WindowsUpdateService> logger) : IWindowsUpdateService
{
    internal const long MaximumExecutableBytes = 512L * 1024 * 1024;
    internal const string PublishedExecutableNamePattern = @"\ABiliCinema(?:-(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)-win-x64)?\.exe\z";
    private static readonly SearchValues<char> HexCharacters = SearchValues.Create("0123456789abcdefABCDEF");
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly IApplicationLifecycle _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
    private readonly ILogger<WindowsUpdateService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private int _installing;

    public async Task InstallAsync(
        GitHubRelease release,
        IProgress<UpdateProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(progress);
        cancellationToken.ThrowIfCancellationRequested();
        var target = GetPublishedExecutable();
        await InstallCoreAsync(target, release, progress, cancellationToken).ConfigureAwait(false);
    }

    internal async Task InstallCoreAsync(
        string target,
        GitHubRelease release,
        IProgress<UpdateProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(progress);
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _installing, 1, 0) != 0)
        {
            throw new InvalidOperationException("另一个更新正在进行中。");
        }
        try
        {
            var directory = Path.Combine(Path.GetDirectoryName(target)!, $".bilicinema-update-{Guid.NewGuid():N}");
            var journal = new WindowsUpdateJournal
            {
                ParentProcessId = Environment.ProcessId,
                TargetPath = target,
                PackagePath = Path.Combine(directory, "BiliCinema.exe"),
                BackupPath = Path.Combine(directory, "previous.exe"),
                HelperPath = Path.Combine(directory, "install.ps1"),
                HelperReadyPath = Path.Combine(directory, "helper-ready"),
                Phase = "preparing"
            };
            var journalPath = Path.Combine(directory, "update.json");
            Directory.CreateDirectory(directory);
            // All operation-created paths are owned by this journal before their first write.
            var handedOff = false;
            Process? helper = null;
            try
            {
                await SaveJournalAsync(journalPath, journal, cancellationToken).ConfigureAwait(false);
                progress.Report(new UpdateProgress("正在获取更新校验信息…", 0));
                var executable = RequireExecutableAsset(release);
                var sums = RequireAsset(release, "SHA256SUMS.txt");
                using (var response = await _httpClient.GetAsync(
                           RequireReleaseAssetUri(release, sums),
                           HttpCompletionOption.ResponseHeadersRead,
                           cancellationToken).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    await response.Content.LoadIntoBufferAsync(1024 * 1024, cancellationToken).ConfigureAwait(false);
                    var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    journal.ExpectedSha256 = ParseChecksum(text, executable.Name);
                }

                await SaveJournalAsync(journalPath, journal, cancellationToken).ConfigureAwait(false);
                await DownloadPackageAsync(release, executable, journal, progress, cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(journal.HelperPath, WindowsUpdateHelper.Script, cancellationToken).ConfigureAwait(false);
                helper = Process.Start(CreateHelperStartInfo(journal.HelperPath, journalPath))
                    ?? throw new InvalidOperationException("无法启动更新安装程序，请关闭后重试。");
                await WaitForHelperReadyAsync(helper, journal.HelperReadyPath, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                progress.Report(new UpdateProgress("更新已校验，正在保存当前任务并关闭应用…", 100, CanCancel: false));

                // The lifecycle owner must finish quiescing downloads/settings before the
                // helper is armed. Shutdown failure leaves the installed file untouched.
                await _lifecycle.RequestShutdownAsync(CancellationToken.None).ConfigureAwait(false);
                journal.Phase = "ready";
                await SaveJournalAsync(journalPath, journal, CancellationToken.None).ConfigureAwait(false);
                handedOff = true;
                await _lifecycle.ExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                if (!handedOff)
                {
                    try
                    {
                        if (helper is { HasExited: false })
                        {
                            helper.Kill();
                            await helper.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                        }

                        Directory.Delete(directory, recursive: true);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                        or System.ComponentModel.Win32Exception or InvalidOperationException)
                    {
                        // The durable journal retains ownership when cleanup is denied.
                        _logger.LogErrorMessage("Update staging cleanup failed; its recovery journal was retained.", exception);
                    }
                }

                helper?.Dispose();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _installing, 0);
        }
    }

    internal async Task DownloadPackageAsync(
        GitHubRelease release,
        GitHubReleaseAsset asset,
        WindowsUpdateJournal journal,
        IProgress<UpdateProgress> progress,
        CancellationToken cancellationToken)
    {
        if (asset.Size is <= 0 or > MaximumExecutableBytes)
        {
            throw new InvalidDataException("更新文件大小无效，请稍后重试。");
        }

        using var response = await _httpClient.GetAsync(
            RequireReleaseAssetUri(release, asset),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } length && length != asset.Size)
        {
            throw new InvalidDataException("更新文件大小与发布信息不一致，已停止更新。");
        }

        var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var inputLifetime = input.ConfigureAwait(false);
        var output = new FileStream(journal.PackagePath, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (output.ConfigureAwait(false))
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long downloaded = 0;
            while (true)
            {
                var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                downloaded += count;
                if (downloaded > asset.Size)
                {
                    throw new InvalidDataException("更新文件超出预期大小，已停止更新。");
                }

                hash.AppendData(buffer, 0, count);
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                progress.Report(new UpdateProgress("正在下载更新…", downloaded * 100d / asset.Size));
            }

            if (downloaded != asset.Size
                || !Convert.ToHexString(hash.GetHashAndReset()).Equals(journal.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("更新文件校验失败，原版本已保留。请重试。");
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal static GitHubReleaseAsset RequireExecutableAsset(GitHubRelease release)
    {
        var version = VersionCheckerService.NormalizeReleaseTag(release.TagName);
        var name = $"BiliCinema-{version}-win-x64.exe";
        return release.Assets.Any(asset => asset.Name.Equals(name, StringComparison.Ordinal))
            ? RequireAsset(release, name)
            : RequireAsset(release, "BiliCinema.exe");
    }

    internal static GitHubReleaseAsset RequireAsset(GitHubRelease release, string name)
    {
        var assets = release.Assets.Where(asset => asset.Name.Equals(name, StringComparison.Ordinal)).ToArray();
        return assets.Length == 1
            ? assets[0]
            : throw new InvalidDataException("此版本尚未提供完整的 Windows 更新文件，请稍后重试。");
    }

    internal static Uri RequireReleaseAssetUri(GitHubRelease release, GitHubReleaseAsset asset)
    {
        var expected = $"https://github.com/{AppConstant.RepoOwner}/{AppConstant.RepoName}/releases/download/{Uri.EscapeDataString(release.TagName)}/{Uri.EscapeDataString(asset.Name)}";
        if (release.Draft || !release.TagName.StartsWith("bilicinema-v", StringComparison.Ordinal)
            || !DownKyi.Core.Versioning.SemanticVersionPolicy.TryNormalizeIdentity(
                VersionCheckerService.NormalizeReleaseTag(release.TagName), out _)
            || !Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var uri)
            || !uri.AbsoluteUri.Equals(expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException("更新下载地址与本项目发布来源不一致，已停止更新。");
        }

        return uri;
    }

    internal static string ParseChecksum(string checksums, string assetName)
    {
        string? result = null;
        foreach (var line in checksums.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(' ', StringComparison.Ordinal);
            if (separator != 64 || line.AsSpan(0, 64).ContainsAnyExcept(HexCharacters))
            {
                continue;
            }

            var name = line[(separator + 1)..].TrimStart(' ', '*');
            if (!name.Equals(assetName, StringComparison.Ordinal))
            {
                continue;
            }

            if (result != null)
            {
                throw new InvalidDataException("更新校验信息存在重复记录，已停止更新。");
            }

            result = line[..64];
        }

        return result ?? throw new InvalidDataException("更新校验信息缺失，已停止更新。");
    }

    internal static bool IsPublishedExecutable(string? executablePath, string? entryAssemblyPath) =>
        !string.IsNullOrEmpty(executablePath)
        && PublishedExecutableName().IsMatch(Path.GetFileName(executablePath))
        && string.IsNullOrEmpty(entryAssemblyPath);

    [GeneratedRegex(PublishedExecutableNamePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PublishedExecutableName();

    private static string GetPublishedExecutable()
    {
        var path = Environment.ProcessPath;
        if (!OperatingSystem.IsWindows()
            || !IsPublishedExecutable(path, Assembly.GetEntryAssembly()?.Location))
        {
            throw new InvalidOperationException("当前为开发运行环境，不能自动替换程序。请使用发布的 Windows 安装包更新。");
        }

        return Path.GetFullPath(path!);
    }

    private static async Task SaveJournalAsync(string path, WindowsUpdateJournal journal, CancellationToken cancellationToken)
    {
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await using var streamLifetime = stream.ConfigureAwait(false);
        await JsonSerializer.SerializeAsync(stream, journal, WindowsUpdateJsonContext.Default.WindowsUpdateJournal,
            cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WaitForHelperReadyAsync(Process helper, string readyPath, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        while (!File.Exists(readyPath))
        {
            if (helper.HasExited || Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(10))
            {
                throw new InvalidOperationException("无法启动更新安装程序，请检查系统策略后重试。");
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static ProcessStartInfo CreateHelperStartInfo(string scriptPath, string journalPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "-Journal", journalPath })
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}

internal sealed class WindowsUpdateJournal
{
    public int ParentProcessId { get; set; }
    public string TargetPath { get; set; } = string.Empty;
    public string PackagePath { get; set; } = string.Empty;
    public string BackupPath { get; set; } = string.Empty;
    public string HelperPath { get; set; } = string.Empty;
    public string HelperReadyPath { get; set; } = string.Empty;
    public string ExpectedSha256 { get; set; } = string.Empty;
    public string Phase { get; set; } = string.Empty;
    public string FailureStep { get; set; } = string.Empty;
    public string FailureCode { get; set; } = string.Empty;
    public string FailureType { get; set; } = string.Empty;
    public int FailureHResult { get; set; }
    public string RecoveryFailureCode { get; set; } = string.Empty;
    public string RecoveryFailureType { get; set; } = string.Empty;
    public int RecoveryFailureHResult { get; set; }
}

[JsonSerializable(typeof(WindowsUpdateJournal))]
internal sealed partial class WindowsUpdateJsonContext : JsonSerializerContext;
