using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using DownKyi.Application.Desktop;
using DownKyi.Application.Diagnostics;
using DownKyi.Commands;
using DownKyi.Models;
using DownKyi.Services;
using DownKyi.Utils;
using Microsoft.Extensions.Logging;

namespace DownKyi.ViewModels.Dialogs;

internal sealed class DownloadRuntimeFailureDialogViewModel : BaseDialogViewModel
{
    private const string IssueTitle = "Download system failed to initialize";
    private const string TruncatedDiagnosticSuffix =
        "\n\n[Diagnostic text truncated to keep the pre-filled issue URL within a safe length. " +
        "Use Copy Error Details for the complete text.]";

    private readonly IApplicationLogService _logService;
    private readonly IClipboardService _clipboardService;
    private readonly IPlatformLauncher _platformLauncher;
    private readonly IUserNotificationService _notifications;
    private readonly ILogger<DownloadRuntimeFailureDialogViewModel> _logger;
    private DownKyiAsyncDelegateCommand? _copyErrorDetailsCommand;
    private DownKyiAsyncDelegateCommand? _createGitHubIssueCommand;
    private string _diagnosticText = string.Empty;

    public DownloadRuntimeFailureDialogViewModel(
        IApplicationLogService logService,
        IClipboardService clipboardService,
        IPlatformLauncher platformLauncher,
        IUserNotificationService notifications,
        ILogger<DownloadRuntimeFailureDialogViewModel> logger)
    {
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _clipboardService = clipboardService
            ?? throw new ArgumentNullException(nameof(clipboardService));
        _platformLauncher = platformLauncher
            ?? throw new ArgumentNullException(nameof(platformLauncher));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Title = DictionaryResource.GetString("DownloadRuntimeFailureTitle");
    }

    public string DiagnosticText
    {
        get => _diagnosticText;
        private set => SetProperty(ref _diagnosticText, value);
    }

    public DownKyiAsyncDelegateCommand CopyErrorDetailsCommand =>
        _copyErrorDetailsCommand ??= new DownKyiAsyncDelegateCommand(
            CopyErrorDetailsAsync,
            _logger);

    public DownKyiAsyncDelegateCommand CreateGitHubIssueCommand =>
        _createGitHubIssueCommand ??= new DownKyiAsyncDelegateCommand(
            CreateGitHubIssueAsync,
            _logger);

    public override void OnDialogOpened(AppDialogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var failure = GetRequiredParameter<Exception>(request, "failure");
        DiagnosticText = CreateDiagnosticText(failure);
    }

    internal async Task CopyErrorDetailsAsync()
    {
        await _clipboardService.SetTextAsync(DiagnosticText).ConfigureAwait(true);
        _notifications.Show(DictionaryResource.GetString("ErrorDetailsCopied"));
    }

    internal async Task CreateGitHubIssueAsync()
    {
        var issueUri = GitHubIssueUriBuilder.Create(
            IssueTitle,
            DiagnosticText,
            TruncatedDiagnosticSuffix);
        if (!await _platformLauncher.OpenUriAsync(issueUri).ConfigureAwait(true))
        {
            _notifications.Show(DictionaryResource.GetString("OpenGitHubIssueFailed"));
        }
    }

    private string CreateDiagnosticText(Exception failure)
    {
        var exceptionText = RedactDiagnosticText(failure.ToString());
        var operatingSystem = RedactDiagnosticText(
            RuntimeInformation.OSDescription);
        return $"""
            DownKyi version: {new AppInfo().VersionName}
            Operating system: {operatingSystem}
            Architecture: {RuntimeInformation.OSArchitecture}
            Failure boundary: Download bootstrap

            {exceptionText}
            """;
    }

    private string RedactDiagnosticText(string? text)
    {
        var resourceRedacted = ExternalResourceRedactor.Redact(text);
        return _logService.RedactDiagnosticText(resourceRedacted);
    }
}
