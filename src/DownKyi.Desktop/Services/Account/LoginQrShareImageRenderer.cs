using System;
using QRCoder;
using SkiaSharp;

namespace DownKyi.Services.Account;

internal static class LoginQrShareImageRenderer
{
    public static byte[] Render(Uri loginUri)
    {
        ArgumentNullException.ThrowIfNull(loginUri);
        if (!loginUri.IsAbsoluteUri)
        {
            throw new ArgumentException("登录二维码地址必须是绝对地址。", nameof(loginUri));
        }

        using var generator = new QRCodeGenerator();
        using var qrData = generator.CreateQrCode(
            loginUri.AbsoluteUri,
            QRCodeGenerator.ECCLevel.H,
            forceUtf8: true,
            utf8BOM: false,
            eciMode: QRCodeGenerator.EciMode.Utf8,
            requestedVersion: 11);
        using var qrCode = new PngByteQRCode(qrData);
        using var qrBitmap = SKBitmap.Decode(qrCode.GetGraphic(20))
                             ?? throw new InvalidOperationException("二维码图片生成失败。");

        const int margin = 30;
        const int footer = 100;
        var width = qrBitmap.Width + margin * 2;
        var height = qrBitmap.Height + margin * 2 + footer;
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(qrBitmap, margin, margin);

        using var typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold);
        using var font = new SKFont(typeface ?? SKTypeface.Default, 36);
        using var paint = new SKPaint { Color = SKColor.Parse("#1685C5"), IsAntialias = true };
        canvas.DrawText("BiliCinema", margin, qrBitmap.Height + margin + 60,
            SKTextAlign.Left, font, paint);

        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 100)
                        ?? throw new InvalidOperationException("二维码图片编码失败。");
        return png.ToArray();
    }
}
