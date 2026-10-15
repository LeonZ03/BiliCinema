using Avalonia.Media.Imaging;
using DownKyi.Application.Bilibili;
using DownKyi.Application.Desktop;
using DownKyi.Core.BiliApi.Login.Models;
using DownKyi.Core.Storage;
using DownKyi.Services.Account;
using DownKyi.Utils;
using DownKyi.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class ViewLoginViewModelTests
{
    [Theory]
    [InlineData(BilibiliHttpFailureKind.UnsupportedProtocol)]
    [InlineData(BilibiliHttpFailureKind.NameResolution)]
    [InlineData(BilibiliHttpFailureKind.SecureConnection)]
    [InlineData(BilibiliHttpFailureKind.Proxy)]
    public async Task QrInitializationNetworkFailureIsVisibleAndKeepsLoginPageAlive(BilibiliHttpFailureKind kind)
    {
        var notification = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var interactions = new TestDesktopInteractionContext();
        interactions.Notifications.NotificationRaised += (_, e) => notification.TrySetResult(e.Message);
        var failure = new BilibiliHttpRequestException("Fixture login transport failure.", kind);
        using var coordinator = new StubLoginCoordinator
        {
            LoginUrlHandler = _ => Task.FromException<LoginUrlOrigin?>(failure)
        };
        using var viewModel = new ViewLoginViewModel(interactions, coordinator,
            new StubLoginQrCodeRenderer(), NullLogger<ViewLoginViewModel>.Instance);

        viewModel.OnNavigatedTo(new AppNavigationContext(AppNavigationRegion.Main, AppRoute.Login,
            AppRoute.Index, null, new AppNavigationParameters()));

        var message = await notification.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(LoginFailureMessage.FromException(failure), message);
        Assert.Null(viewModel.LoginQrCode);
        Assert.True(viewModel.BrowserCookieLoginCommand.CanExecute(null));
    }

    [Fact]
    public async Task TransportCancellationShowsLoginFailureWhenLoginTokenIsActive()
    {
        var notifications = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var interactions = new TestDesktopInteractionContext();
        interactions.Notifications.NotificationRaised += (_, e) =>
            notifications.TrySetResult(e.Message);
        var observedCancellationToken = CancellationToken.None;
        using var coordinator = new StubLoginCoordinator
        {
            CommitHandler = (_, cancellationToken) =>
            {
                observedCancellationToken = cancellationToken;
                return Task.FromException<bool>(
                    new OperationCanceledException("Fixture transport timeout."));
            }
        };
        using var viewModel = new ViewLoginViewModel(
            interactions,
            coordinator,
            new StubLoginQrCodeRenderer(),
            NullLogger<ViewLoginViewModel>.Instance)
        {
            BrowserCookieHeader = "SESSDATA=fixture%2Fsession"
        };
        var commandEnabled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var command = viewModel.BrowserCookieLoginCommand;
        command.CanExecuteChanged += (_, _) =>
        {
            if (command.CanExecute(null))
            {
                commandEnabled.TrySetResult();
            }
        };

        command.Execute(null);

        var message = await notifications.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DictionaryResource.GetString("LoginFailed"), message);
        Assert.True(observedCancellationToken.CanBeCanceled);
        Assert.False(observedCancellationToken.IsCancellationRequested);
        Assert.Equal(string.Empty, viewModel.BrowserCookieHeader);

        viewModel.ExecuteBackSpace();
        await commandEnabled.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(command.CanExecute(null));
    }

    private sealed class StubLoginCoordinator : ILoginCoordinator
    {
        public Func<CancellationToken, Task<LoginUrlOrigin?>>? LoginUrlHandler { get; init; }
        public Func<IReadOnlyList<DownKyiCookie>, CancellationToken, Task<bool>>? CommitHandler
        {
            get;
            init;
        }

        public Task<LoginUrlOrigin?> RequestLoginUrlAsync(CancellationToken cancellationToken)
        {
            return LoginUrlHandler?.Invoke(cancellationToken) ?? throw new NotSupportedException();
        }

        public Task<LoginStatusResult?> GetLoginStatusAsync(
            string qrcodeKey,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<bool> SaveLoginCookiesAsync(
            LoginStatusResult loginStatus,
            Uri redirectUri,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<bool> CommitLoginCookiesAsync(
            IReadOnlyList<DownKyiCookie> cookies,
            CancellationToken cancellationToken)
        {
            return CommitHandler?.Invoke(cookies, cancellationToken)
                   ?? throw new InvalidOperationException("No commit handler was configured.");
        }

        public void Dispose()
        {
        }
    }

    private sealed class StubLoginQrCodeRenderer : ILoginQrCodeRenderer
    {
        public Bitmap Render(Uri loginUri)
        {
            throw new NotSupportedException();
        }
    }
}
