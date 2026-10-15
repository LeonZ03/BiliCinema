using DownKyi.Application.Desktop;
using DownKyi.Models;
using DownKyi.ViewModels.Dialogs;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class ManualUpdateDialogContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SkipCommandMatchesExplicitDialogMode(bool enableSkipVersion)
    {
        using var settings = new TestSettingsStore();
        using var viewModel = CreateViewModel(settings);
        var closeRequested = false;
        viewModel.CloseRequested += (_, _) => closeRequested = true;

        viewModel.OnDialogOpened(CreateRequest(enableSkipVersion));

        Assert.Equal(enableSkipVersion, viewModel.EnableSkipVersionOnLaunch);
        Assert.Equal(enableSkipVersion, viewModel.SkipCurrentVersionCommand.CanExecute(null));

        viewModel.SkipCurrentVersionCommand.Execute(null);

        Assert.Equal(
            enableSkipVersion ? "2.0.0" : string.Empty,
            settings.Store.Current.About.SkipVersionOnLaunch);
        Assert.Equal(enableSkipVersion, closeRequested);
    }

    [Fact]
    public void MissingDialogModeFailsClosed()
    {
        using var settings = new TestSettingsStore();
        using var viewModel = CreateViewModel(settings);
        var request = new AppDialogRequest(
            AppDialog.NewVersionAvailable,
            new Dictionary<string, object?> { ["release"] = CreateRelease() });

        viewModel.OnDialogOpened(request);

        Assert.False(viewModel.EnableSkipVersionOnLaunch);
        Assert.False(viewModel.SkipCurrentVersionCommand.CanExecute(null));
        viewModel.SkipCurrentVersionCommand.Execute(null);
        Assert.Empty(settings.Store.Current.About.SkipVersionOnLaunch);
    }

    private static NewVersionAvailableDialogViewModel CreateViewModel(
        TestSettingsStore settings)
    {
        return new NewVersionAvailableDialogViewModel(
            settings.Store,
            new UnusedPlatformLauncher(),
            NullLogger<NewVersionAvailableDialogViewModel>.Instance);
    }

    private static AppDialogRequest CreateRequest(bool enableSkipVersion)
    {
        return new AppDialogRequest(
            AppDialog.NewVersionAvailable,
            new Dictionary<string, object?>
            {
                ["release"] = CreateRelease(),
                ["enableSkipVersion"] = enableSkipVersion
            });
    }

    private static GitHubRelease CreateRelease()
    {
        return new GitHubRelease
        {
            TagName = "v2.0.0",
            Body = "Release notes"
        };
    }

    private sealed class UnusedPlatformLauncher : IPlatformLauncher
    {
        public Task<bool> OpenFileAsync(
            string path,
            CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<bool> OpenFolderAsync(
            string path,
            CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<bool> OpenUriAsync(
            Uri uri,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}
