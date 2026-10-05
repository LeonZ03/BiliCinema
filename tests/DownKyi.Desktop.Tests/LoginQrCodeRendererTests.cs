using Avalonia.Headless.XUnit;
using DownKyi.Services.Account;
using SkiaSharp;

namespace DownKyi.Desktop.Tests;

public sealed class LoginQrCodeRendererTests
{
    [AvaloniaFact]
    public async Task RendererCreatesAUsableBitmapForAnAbsoluteLoginUri()
    {
        await AvaloniaTestDispatcher.RunAsync(() =>
        {
            using var bitmap = new LoginQrCodeRenderer().Render(
                new Uri("https://passport.bilibili.com/login?test=contract"));

            Assert.True(bitmap.PixelSize.Width > 0);
            Assert.True(bitmap.PixelSize.Height > 0);
            Assert.Equal(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
        }).ConfigureAwait(true);
    }

    [AvaloniaFact]
    public async Task RendererRejectsRelativeUris()
    {
        await AvaloniaTestDispatcher.RunAsync(() =>
        {
            var renderer = new LoginQrCodeRenderer();

            Assert.Throws<ArgumentException>(() =>
                renderer.Render(new Uri("/relative", UriKind.Relative)));
        }).ConfigureAwait(true);
    }

    [AvaloniaFact]
    public async Task ShareImageKeepsQrAtFullSizeAndAddsWatermarkFooter()
    {
        await AvaloniaTestDispatcher.RunAsync(() =>
        {
            var image = LoginQrShareImageRenderer.Render(
                new Uri("https://passport.bilibili.com/login?test=share"));
            using var shared = SKBitmap.Decode(image);

            Assert.NotNull(shared);
            Assert.True(shared.Width > 1000);
            Assert.Equal(shared.Width + 100, shared.Height);
            Assert.True(image.Length > 1_000);
        }).ConfigureAwait(true);
    }
}
