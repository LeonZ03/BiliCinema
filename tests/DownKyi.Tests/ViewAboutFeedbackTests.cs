using DownKyi.Application.Desktop;
using DownKyi.Application.Diagnostics;
using DownKyi.Services;
using DownKyi.ViewModels.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class ViewAboutFeedbackTests
{
    [Fact]
    public async Task FeedbackPrefillsTwoUserSectionsAndRedactedDiagnostics()
    {
        var logs = new RecordingLogService(
        [
            CreateRecord(LogLevel.Information, "informational context"),
            CreateRecord(
                LogLevel.Error,
                "request failed with secret-token",
                "InvalidOperationException: secret-token")
        ]);
        var launcher = new RecordingPlatformLauncher();
        using var settings = new TestSettingsStore();
        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.github.test/")
        };
        using var viewModel = new ViewAboutViewModel(
            new TestDesktopInteractionContext(),
            settings.Store,
            logs,
            launcher,
            new VersionCheckerService(httpClient, "owner", "repo"),
            NullLogger<ViewAboutViewModel>.Instance);

        await viewModel.ExecuteFeedbackCommand();

        Assert.Equal("github.com", launcher.Uri?.Host);
        Assert.Equal("/crazysmile-PhD/downkyicore/issues/new", launcher.Uri?.AbsolutePath);
        Assert.Equal("[Bug] 用户反馈", GetQueryValue(launcher.Uri!, "title"));
        var body = GetQueryValue(launcher.Uri!, "body");
        Assert.Equal(2, CountOccurrences(body, "## "));
        Assert.Contains("## 发生什么", body, StringComparison.Ordinal);
        Assert.Contains("## 如何复现", body, StringComparison.Ordinal);
        Assert.Contains("自动诊断信息（已脱敏）", body, StringComparison.Ordinal);
        Assert.Contains("DownKyi version:", body, StringComparison.Ordinal);
        Assert.Contains("Operating system:", body, StringComparison.Ordinal);
        Assert.Contains("Architecture:", body, StringComparison.Ordinal);
        Assert.Contains("request failed with [redacted]", body, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException: [redacted]", body, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", body, StringComparison.Ordinal);
        Assert.DoesNotContain("informational context", body, StringComparison.Ordinal);
        Assert.False(logs.ExportCalled);
    }

    [Fact]
    public async Task FeedbackKeepsUserSectionsWhenDiagnosticsExceedSafeUriLength()
    {
        var logs = new RecordingLogService(
        [
            CreateRecord(LogLevel.Error, new string('错', 10_000))
        ]);
        var launcher = new RecordingPlatformLauncher();
        using var settings = new TestSettingsStore();
        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.github.test/")
        };
        using var viewModel = new ViewAboutViewModel(
            new TestDesktopInteractionContext(),
            settings.Store,
            logs,
            launcher,
            new VersionCheckerService(httpClient, "owner", "repo"),
            NullLogger<ViewAboutViewModel>.Instance);

        await viewModel.ExecuteFeedbackCommand();

        Assert.True(launcher.Uri!.AbsoluteUri.Length <= 8_000);
        var body = GetQueryValue(launcher.Uri, "body");
        Assert.StartsWith("## 发生什么", body, StringComparison.Ordinal);
        Assert.Contains("## 如何复现", body, StringComparison.Ordinal);
        Assert.EndsWith(
            "[自动诊断信息过长，已截断。可使用应用内的“导出诊断日志”取得完整的近期日志。]",
            body,
            StringComparison.Ordinal);
    }

    private static ApplicationLogRecord CreateRecord(
        LogLevel level,
        string message,
        string exceptionText = "")
    {
        return new ApplicationLogRecord(
            new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero),
            level,
            "Feedback.Tests",
            new EventId(17, "Feedback"),
            message,
            string.IsNullOrEmpty(exceptionText) ? string.Empty : "InvalidOperationException",
            42,
            7,
            string.Empty,
            exceptionText);
    }

    private static int CountOccurrences(string value, string token)
    {
        return value.Split(token, StringSplitOptions.None).Length - 1;
    }

    private static string GetQueryValue(Uri uri, string name)
    {
        foreach (var field in uri.Query.TrimStart('?').Split('&'))
        {
            var parts = field.Split('=', 2);
            if (parts.Length == 2 && string.Equals(parts[0], name, StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(parts[1]);
            }
        }

        throw new InvalidOperationException($"Query parameter '{name}' was not found.");
    }

    private sealed class RecordingLogService(
        IReadOnlyList<ApplicationLogRecord> records) : IApplicationLogService
    {
        public string LogDirectory => string.Empty;

        public bool ExportCalled { get; private set; }

        public IReadOnlyList<ApplicationLogRecord> GetRecentEvents() => records;

        public ApplicationLogMetrics GetMetrics() =>
            new(0, 0, 0, 0, 0, 0, 0, 0, null);

        public string RedactDiagnosticText(string? text) =>
            (text ?? string.Empty).Replace(
                "secret-token",
                "[redacted]",
                StringComparison.Ordinal);

        public Task FlushAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<string> ExportDiagnosticLogAsync(
            CancellationToken cancellationToken = default)
        {
            ExportCalled = true;
            throw new NotSupportedException();
        }
    }

    private sealed class RecordingPlatformLauncher : IPlatformLauncher
    {
        public Uri? Uri { get; private set; }

        public Task<bool> OpenFileAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> OpenFolderAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> OpenUriAsync(
            Uri uri,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Uri = uri;
            return Task.FromResult(true);
        }
    }
}
