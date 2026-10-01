using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Application.Diagnostics;
using DownKyi.Commands;
using DownKyi.Core.Settings;
using DownKyi.Models;
using DownKyi.Services;
using DownKyi.Utils;
using DownKyi.ViewModels.Dialogs;
using Microsoft.Extensions.Logging;

namespace DownKyi.ViewModels.Settings;

internal class ViewAboutViewModel : ViewModelBase
{
    public const string Tag = "PageSettingsAbout";
    private const int MaximumFeedbackEventCount = 20;
    private const string FeedbackIssueTitle = "[Bug] 用户反馈";
    private const string TruncatedFeedbackSuffix =
        "\n\n[自动诊断信息过长，已截断。可使用应用内的“导出诊断日志”取得完整的近期日志。]";

    private readonly ISettingsStore _settingsStore;
    private readonly IApplicationLogService _logService;
    private readonly ILogger<ViewAboutViewModel> _logger;
    private readonly IPlatformLauncher _platformLauncher;
    private readonly VersionCheckerService _versionChecker;
    private bool _isOnNavigatedTo;

    #region 页面属性申明

    private string _appName = string.Empty;

    public string AppName
    {
        get => _appName;
        set => SetProperty(ref _appName, value);
    }

    private string _appVersion = string.Empty;

    public string AppVersion
    {
        get => _appVersion;
        set => SetProperty(ref _appVersion, value);
    }

    private bool _isReceiveBetaVersion;

    public bool IsReceiveBetaVersion
    {
        get => _isReceiveBetaVersion;
        set => SetProperty(ref _isReceiveBetaVersion, value);
    }

    private bool _autoUpdateWhenLaunch;

    public bool AutoUpdateWhenLaunch
    {
        get => _autoUpdateWhenLaunch;
        set => SetProperty(ref _autoUpdateWhenLaunch, value);
    }

    #endregion

    public ViewAboutViewModel(
        IDesktopInteractionContext desktopInteractions,
        ISettingsStore settingsStore,
        IApplicationLogService logService,
        IPlatformLauncher platformLauncher,
        VersionCheckerService versionChecker,
        ILogger<ViewAboutViewModel> logger) : base(desktopInteractions)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _platformLauncher = platformLauncher ?? throw new ArgumentNullException(nameof(platformLauncher));
        _versionChecker = versionChecker ?? throw new ArgumentNullException(nameof(versionChecker));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        #region 属性初始化

        var app = new AppInfo();
        AppName = app.Name;
        AppVersion = app.VersionName;

        #endregion
    }

    /// <summary>
    /// 导航到页面时执行
    /// </summary>
    /// <param name="navigationContext"></param>
    public override void OnNavigatedTo(AppNavigationContext navigationContext)
    {
        base.OnNavigatedTo(navigationContext);

        _isOnNavigatedTo = true;

        // 是否接收测试版更新
        var about = _settingsStore.Current.About;
        var isReceiveBetaVersion = about.IsReceiveBetaVersion;
        IsReceiveBetaVersion = isReceiveBetaVersion == AllowStatus.Yes;

        // 是否在启动时自动检查更新
        var isAutoUpdateWhenLaunch = about.AutoUpdateWhenLaunch;
        AutoUpdateWhenLaunch = isAutoUpdateWhenLaunch == AllowStatus.Yes;

        _isOnNavigatedTo = false;
    }

    #region 命令申明

    // 访问主页事件
    private DownKyiAsyncDelegateCommand? _appNameCommand;

    public DownKyiAsyncDelegateCommand AppNameCommand => _appNameCommand ??= new DownKyiAsyncDelegateCommand(ExecuteAppNameCommand, _logger);

    /// <summary>
    /// 访问主页事件
    /// </summary>
    private async Task ExecuteAppNameCommand()
    {
        await OpenUriAsync($"https://github.com/{AppConstant.RepoOwner}/{AppConstant.RepoName}/releases").ConfigureAwait(true);
    }

    // 检查更新事件
    private ICommand? _checkUpdateCommand;

    public ICommand CheckUpdateCommand => _checkUpdateCommand ??= new DownKyiAsyncDelegateCommand(ExecuteCheckUpdateCommand, _logger);


    /// <summary>
    /// 检查更新事件
    /// </summary>
    private async Task ExecuteCheckUpdateCommand()
    {
        try
        {
            var release = await _versionChecker
                .GetLatestReleaseAsync(_isReceiveBetaVersion)
                .ConfigureAwait(true);
            if (GitHubRelease.IsNullOrEmpty(release))
            {
                Notifications.Show("检查失败，请稍后重试~");
                return;
            }

            if (_versionChecker.IsNewVersionAvailable(release!.TagName))
            {
                await AppDialogs.ShowAsync(new AppDialogRequest(
                    AppDialog.NewVersionAvailable,
                    new Dictionary<string, object?>
                    {
                        ["release"] = release,
                        ["enableSkipVersion"] = false
                    })).ConfigureAwait(true);
            }
            else
            {
                Notifications.Show("已是最新版~");
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarningMessage("Manual update check timed out.");
            Notifications.Show("检查超时，请稍后重试~");
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or JsonException)
        {
            _logger.LogErrorMessage("Manual update check failed.", e);
            Notifications.Show("检查失败，请稍后重试~");
        }
    }

    // 意见反馈事件
    private DownKyiAsyncDelegateCommand? _feedbackCommand;

    public DownKyiAsyncDelegateCommand FeedbackCommand => _feedbackCommand ??= new DownKyiAsyncDelegateCommand(ExecuteFeedbackCommand, _logger);

    /// <summary>
    /// 意见反馈事件
    /// </summary>
    internal async Task ExecuteFeedbackCommand()
    {
        var issueBody = CreateFeedbackIssueBody();
        var issueUri = GitHubIssueUriBuilder.Create(
            FeedbackIssueTitle,
            issueBody,
            TruncatedFeedbackSuffix);
        await OpenUriAsync(issueUri).ConfigureAwait(true);
    }

    // 打开日志目录事件
    private DownKyiAsyncDelegateCommand? _openLogsCommand;

    public DownKyiAsyncDelegateCommand OpenLogsCommand => _openLogsCommand ??= new DownKyiAsyncDelegateCommand(ExecuteOpenLogsCommand, _logger);

    private async Task ExecuteOpenLogsCommand()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await _logService.FlushAsync(cancellation.Token).ConfigureAwait(true);
        if (!await _platformLauncher.OpenFolderAsync(_logService.LogDirectory).ConfigureAwait(true))
        {
            Notifications.Show("无法打开日志文件夹");
        }
    }

    // 导出脱敏诊断日志事件
    private DownKyiAsyncDelegateCommand? _exportDiagnosticLogCommand;

    public DownKyiAsyncDelegateCommand ExportDiagnosticLogCommand =>
        _exportDiagnosticLogCommand ??= new DownKyiAsyncDelegateCommand(ExecuteExportDiagnosticLogCommand, _logger);

    private async Task ExecuteExportDiagnosticLogCommand()
    {
        try
        {
            var diagnosticLog = await _logService.ExportDiagnosticLogAsync().ConfigureAwait(true);
            Notifications.Show(DictionaryResource.GetString("DiagnosticLogExported"));
            if (!await _platformLauncher.OpenFileAsync(diagnosticLog).ConfigureAwait(true))
            {
                Notifications.Show("无法打开诊断日志");
            }
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException
            or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogErrorMessage("Diagnostic log export failed.", e);
            Notifications.Show(DictionaryResource.GetString("DiagnosticLogExportFailed"));
        }
    }

    // 是否接收测试版更新事件
    private RelayCommand? _receiveBetaVersionCommand;

    public RelayCommand ReceiveBetaVersionCommand => _receiveBetaVersionCommand ??= new RelayCommand(ExecuteReceiveBetaVersionCommand);

    /// <summary>
    /// 是否接收测试版更新事件
    /// </summary>
    private void ExecuteReceiveBetaVersionCommand()
    {
        var isReceiveBetaVersion = IsReceiveBetaVersion ? AllowStatus.Yes : AllowStatus.No;

        var updated = _settingsStore.Update(settings => settings with
        {
            About = settings.About with { IsReceiveBetaVersion = isReceiveBetaVersion }
        });
        var isSucceed = updated.About.IsReceiveBetaVersion == isReceiveBetaVersion;
        PublishTip(isSucceed);
    }

    // 是否在启动时自动检查更新事件
    private RelayCommand? _autoUpdateWhenLaunchCommand;

    public RelayCommand AutoUpdateWhenLaunchCommand => _autoUpdateWhenLaunchCommand ??= new RelayCommand(ExecuteAutoUpdateWhenLaunchCommand);

    /// <summary>
    /// 是否在启动时自动检查更新事件
    /// </summary>
    private void ExecuteAutoUpdateWhenLaunchCommand()
    {
        var isAutoUpdateWhenLaunch = AutoUpdateWhenLaunch ? AllowStatus.Yes : AllowStatus.No;

        var updated = _settingsStore.Update(settings => settings with
        {
            About = settings.About with { AutoUpdateWhenLaunch = isAutoUpdateWhenLaunch }
        });
        var isSucceed = updated.About.AutoUpdateWhenLaunch == isAutoUpdateWhenLaunch;
        PublishTip(isSucceed);
    }

    private string CreateFeedbackIssueBody()
    {
        var diagnosticText = CreateFeedbackDiagnosticText();
        return $"""
            ## 发生什么

            <!-- 请描述实际发生了什么。 -->

            ## 如何复现

            <!-- 请按顺序列出可以复现问题的步骤。 -->

            <details>
            <summary>自动诊断信息（已脱敏）</summary>

            ```text
            {diagnosticText}
            ```
            </details>
            """;
    }

    private string CreateFeedbackDiagnosticText()
    {
        var builder = new StringBuilder();
        builder.Append("DownKyi version: ").AppendLine(AppVersion);
        builder.Append("Operating system: ").AppendLine(RuntimeInformation.OSDescription);
        builder.Append("Architecture: ").AppendLine(RuntimeInformation.OSArchitecture.ToString());
        builder.AppendLine("Recent warnings and errors (newest first):");

        var events = _logService
            .GetRecentEvents()
            .Where(static record =>
                record.Level >= LogLevel.Warning && record.Level < LogLevel.None)
            .TakeLast(MaximumFeedbackEventCount)
            .Reverse()
            .ToArray();
        if (events.Length == 0)
        {
            builder.AppendLine("None captured.");
        }
        else
        {
            foreach (var record in events)
            {
                builder
                    .Append(record.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
                    .Append(" [")
                    .Append(record.Level)
                    .Append("] ")
                    .Append(record.Category)
                    .Append(": ")
                    .AppendLine(record.Message);
                if (!string.IsNullOrWhiteSpace(record.ExceptionText))
                {
                    builder.AppendLine(record.ExceptionText);
                }
            }
        }

        return _logService.RedactDiagnosticText(builder.ToString().TrimEnd());
    }

    private Task OpenUriAsync(string value)
    {
        return OpenUriAsync(new Uri(value));
    }

    private async Task OpenUriAsync(Uri uri)
    {
        if (!await _platformLauncher.OpenUriAsync(uri).ConfigureAwait(true))
        {
            Notifications.Show("无法打开网页");
        }
    }

    #endregion

    /// <summary>
    /// 发送需要显示的tip
    /// </summary>
    /// <param name="isSucceed"></param>
    private void PublishTip(bool isSucceed)
    {
        if (_isOnNavigatedTo)
        {
            return;
        }

        Notifications.Show(isSucceed
            ? DictionaryResource.GetString("TipSettingUpdated")
            : DictionaryResource.GetString("TipSettingFailed"));
    }
}
