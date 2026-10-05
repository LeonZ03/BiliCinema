using Avalonia.Headless.XUnit;
using DownKyi.Application.Desktop;
using DownKyi.Platform;

namespace DownKyi.Desktop.Tests;

public sealed class WatchPrivacyNavigationTests
{
    [AvaloniaFact]
    public async Task WatchModeCannotOpenPrivateAccountPages()
    {
        await AvaloniaTestDispatcher.RunAsync(() =>
        {
            using var navigation = new AvaloniaNavigationService(
                static _ => new object(),
                static action => action(),
                allowPrivateAccountRoutes: false);
            navigation.Navigate(new AppNavigationRequest(AppRoute.Index));
            var index = navigation.GetActiveView(AppNavigationRegion.Main);

            foreach (var route in new[]
                     {
                         AppRoute.MySpace, AppRoute.MyFavorites, AppRoute.MyBangumiFollow,
                         AppRoute.MyToViewVideo, AppRoute.MyHistory
                     })
            {
                navigation.Navigate(new AppNavigationRequest(route, AppRoute.Index));
                Assert.Same(index, navigation.GetActiveView(AppNavigationRegion.Main));
            }

            navigation.Navigate(new AppNavigationRequest(AppRoute.UserSpace, AppRoute.Index));
            Assert.NotSame(index, navigation.GetActiveView(AppNavigationRegion.Main));
        }).ConfigureAwait(true);
    }
}
