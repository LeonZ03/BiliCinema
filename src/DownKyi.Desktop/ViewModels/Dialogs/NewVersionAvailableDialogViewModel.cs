using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Application.Diagnostics;
using DownKyi.Commands;
using DownKyi.Core.Settings;
using DownKyi.Core.Versioning;
using DownKyi.Models;
using DownKyi.Services;
using DownKyi.Services.Updates;
using Microsoft.Extensions.Logging;

namespace DownKyi.ViewModels.Dialogs
{
    internal sealed class NewVersionAvailableDialogViewModel : BaseDialogViewModel, IDisposable
    {
        public const string Tag = "NewVersionAvailable";

        private readonly ILogger<NewVersionAvailableDialogViewModel> _logger;
        private readonly IPlatformLauncher _platformLauncher;
        private readonly ISettingsStore _settingsStore;
        private readonly IWindowsUpdateService? _updateService;
        private GitHubRelease? _release;
        private CancellationTokenSource? _updateCancellation;
        private Task? _updateTask;
        private bool _isUpdating;
        private string _statusMessage = string.Empty;
        private double _downloadPercentage;
        private bool _canCancelUpdate;
        private DownKyiAsyncDelegateCommand? _allowCommand;
        private RelayCommand? _cancelUpdateCommand;

        public bool IsUpdating
        {
            get => _isUpdating;
            private set
            {
                if (SetProperty(ref _isUpdating, value))
                {
                    _skipCurrentVersionCommand?.NotifyCanExecuteChanged();
                    _cancelUpdateCommand?.NotifyCanExecuteChanged();
                }
            }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            private set => SetProperty(ref _statusMessage, value);
        }

        public double DownloadPercentage
        {
            get => _downloadPercentage;
            private set => SetProperty(ref _downloadPercentage, value);
        }

        public RelayCommand CancelUpdateCommand => _cancelUpdateCommand ??=
            new RelayCommand(() => _updateCancellation?.Cancel(), () => IsUpdating && _canCancelUpdate);

        private RelayCommand? _skipCurrentVersionCommand;

        public RelayCommand SkipCurrentVersionCommand => _skipCurrentVersionCommand ??=
            new RelayCommand(ExecuteSkipCurrentVersionCommand, CanSkipCurrentVersion);
        public DownKyiAsyncDelegateCommand AllowCommand => _allowCommand ??= new DownKyiAsyncDelegateCommand(ExecuteAllowCommand, _logger);

        public NewVersionAvailableDialogViewModel(
            ISettingsStore settingsStore,
            IPlatformLauncher platformLauncher,
            ILogger<NewVersionAvailableDialogViewModel> logger,
            IWindowsUpdateService? updateService = null)
        {
            _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
            _platformLauncher = platformLauncher ?? throw new ArgumentNullException(nameof(platformLauncher));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _updateService = updateService;
        }

        private async Task ExecuteAllowCommand()
        {
            if (_updateService != null && _release != null)
            {
                _updateTask = InstallUpdateAsync(_release);
                await _updateTask.ConfigureAwait(true);
                return;
            }

            var releaseUri = new Uri(
                $"https://github.com/{AppConstant.RepoOwner}/{AppConstant.RepoName}/releases/tag/{TagName}");
            _ = await _platformLauncher.OpenUriAsync(releaseUri).ConfigureAwait(true);
            CloseDialog(AppDialogOutcome.Accepted);
        }

        private async Task InstallUpdateAsync(GitHubRelease release)
        {
            using var cancellation = new CancellationTokenSource();
            _updateCancellation = cancellation;
            IsUpdating = true;
            _canCancelUpdate = true;
            _cancelUpdateCommand?.NotifyCanExecuteChanged();
            StatusMessage = "正在准备更新…";
            try
            {
                var progress = new Progress<UpdateProgress>(update =>
                {
                    StatusMessage = update.Message;
                    DownloadPercentage = update.Percentage;
                    _canCancelUpdate = update.CanCancel;
                    _cancelUpdateCommand?.NotifyCanExecuteChanged();
                });
                await _updateService!.InstallAsync(release, progress, cancellation.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                StatusMessage = "更新已取消，当前版本已保留。";
            }
            catch (Exception exception) when (exception is System.IO.IOException
                or System.Net.Http.HttpRequestException or InvalidOperationException
                or UnauthorizedAccessException or System.ComponentModel.Win32Exception
                or NotSupportedException or OperationCanceledException or TimeoutException)
            {
                _logger.LogErrorMessage("The application update could not be installed.", exception);
                StatusMessage = exception is System.IO.InvalidDataException or InvalidOperationException
                    ? exception.Message
                    : "更新失败，当前版本已保留。请检查网络和安装目录权限后重试。";
            }
            finally
            {
                _updateCancellation = null;
                IsUpdating = false;
            }
        }

        // The committed update must allow the application lifecycle to close the
        // window. Before commit, a close request must cancel the download first.
        public override bool CanCloseDialog() => !IsUpdating || !_canCancelUpdate;

        public override async Task OnDialogClosedAsync()
        {
            if (_updateCancellation != null)
            {
                await _updateCancellation.CancelAsync().ConfigureAwait(true);
            }

            if (_updateTask != null)
            {
                await _updateTask.ConfigureAwait(true);
            }
        }

        public void Dispose()
        {
            _updateCancellation?.Cancel();
            _updateCancellation?.Dispose();
            _updateCancellation = null;
            GC.SuppressFinalize(this);
        }

        private void ExecuteSkipCurrentVersionCommand()
        {
            if (!CanSkipCurrentVersion())
            {
                return;
            }

            _settingsStore.Update(settings => settings with
            {
                About = settings.About with { SkipVersionOnLaunch = NewVersion }
            });
            CloseDialog(AppDialogOutcome.Canceled);
        }

        private bool CanSkipCurrentVersion()
        {
            return EnableSkipVersionOnLaunch && !IsUpdating;
        }

        private string _tagName = string.Empty;

        public string TagName
        {
            get => _tagName;
            set => SetProperty(ref _tagName, value);
        }

        private string _markdownText = string.Empty;

        public string MarkdownText
        {
            get => _markdownText;
            set => SetProperty(ref _markdownText, value);
        }

        private bool _enableSkipVersionOnLaunch;


        private string _newVersion = string.Empty;

        private string NewVersion
        {
            get => _newVersion;
            set => SetProperty(ref _newVersion, value);
        }

        public bool EnableSkipVersionOnLaunch
        {
            get => _enableSkipVersionOnLaunch;
            set
            {
                if (SetProperty(ref _enableSkipVersionOnLaunch, value))
                {
                    _skipCurrentVersionCommand?.NotifyCanExecuteChanged();
                }
            }
        }

        public override void OnDialogOpened(AppDialogRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);

            var release = GetRequiredParameter<GitHubRelease>(request, "release");
            _release = release;
            EnableSkipVersionOnLaunch =
                request.Parameters?.TryGetValue("enableSkipVersion", out var enableSkipVersion) == true &&
                enableSkipVersion is true;
            MarkdownText = release.Body;
            TagName = release.TagName;
            NewVersion = SemanticVersionPolicy.TryNormalizeIdentity(
                VersionCheckerService.NormalizeReleaseTag(release.TagName),
                out var normalizedVersion)
                ? normalizedVersion
                : string.Empty;
        }
    }
}
