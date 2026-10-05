using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Platform;
using QRCoder;
using SkiaSharp;

namespace DownKyi.Services.Account;

internal static class LoginQrShareImageRenderer
{
    private const string IconResource = "avares://DownKyi.Desktop/Resources/favicon.ico";

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
        using var appIcon = LoadAppIcon();

        // Keep the QR pixels and its quiet zone intact for reliable scanning.
        const int cardMargin = 28;
        const int innerMargin = 76;
        const int headerHeight = 200;
        const int introHeight = 175;
        const int qrTopGap = 30;
        const int footerHeight = 220;
        var width = qrBitmap.Width + (cardMargin + innerMargin) * 2;
        var height = cardMargin * 2 + headerHeight + introHeight + qrTopGap
                     + qrBitmap.Height + footerHeight;
        var contentLeft = cardMargin + innerMargin;
        var qrTop = cardMargin + headerHeight + introHeight + qrTopGap;
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;
        canvas.Clear(SKColor.Parse("#E7F4F8"));

        using var cardPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        canvas.DrawRoundRect(new SKRect(cardMargin, cardMargin,
            width - cardMargin, height - cardMargin), 42, 42, cardPaint);

        using var headerPaint = new SKPaint { Color = SKColor.Parse("#EAF8FB"), IsAntialias = true };
        canvas.DrawRoundRect(new SKRect(cardMargin, cardMargin,
            width - cardMargin, cardMargin + headerHeight), 42, 42, headerPaint);
        canvas.DrawRect(new SKRect(cardMargin, cardMargin + headerHeight - 42,
            width - cardMargin, cardMargin + headerHeight), headerPaint);

        const int iconSize = 96;
        var iconTop = cardMargin + (headerHeight - iconSize) / 2;
        canvas.DrawBitmap(appIcon, new SKRect(contentLeft, iconTop,
            contentLeft + iconSize, iconTop + iconSize));

        using var brandFont = CreateFont("Segoe UI", 58, SKFontStyle.Bold);
        using var titleFont = CreateFont("Microsoft YaHei", 54, SKFontStyle.Bold);
        using var bodyFont = CreateFont("Microsoft YaHei", 30, SKFontStyle.Normal);
        using var captionFont = CreateFont("Microsoft YaHei", 24, SKFontStyle.Normal);
        using var brandPaint = new SKPaint { Color = SKColor.Parse("#153C50"), IsAntialias = true };
        using var primaryPaint = new SKPaint { Color = SKColor.Parse("#18384B"), IsAntialias = true };
        using var mutedPaint = new SKPaint { Color = SKColor.Parse("#42687A"), IsAntialias = true };
        using var accentPaint = new SKPaint { Color = SKColor.Parse("#1486BC"), IsAntialias = true };

        canvas.DrawText("BiliCinema", contentLeft + iconSize + 24,
            cardMargin + 116, SKTextAlign.Left, brandFont, brandPaint);
        canvas.DrawText("一起看喜欢的电影", contentLeft + iconSize + 27,
            cardMargin + 157, SKTextAlign.Left, captionFont, mutedPaint);

        var center = width / 2f;
        canvas.DrawText("请帮我扫码登录～", center,
            cardMargin + headerHeight + 79, SKTextAlign.Center, titleFont, primaryPaint);
        canvas.DrawText("打开哔哩哔哩 App，扫描下方二维码", center,
            cardMargin + headerHeight + 136, SKTextAlign.Center, bodyFont, mutedPaint);

        canvas.DrawBitmap(qrBitmap, contentLeft, qrTop);

        var footerTop = qrTop + qrBitmap.Height;
        using var linePaint = new SKPaint { Color = SKColor.Parse("#D8E9EF"), IsAntialias = true };
        canvas.DrawRect(new SKRect(contentLeft, footerTop + 37,
            width - contentLeft, footerTop + 39), linePaint);
        canvas.DrawText("扫码后请在手机上确认登录", center, footerTop + 107,
            SKTextAlign.Center, bodyFont, accentPaint);
        canvas.DrawText("完成后就可以一起看电影啦", center, footerTop + 160,
            SKTextAlign.Center, captionFont, mutedPaint);

        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 100)
                        ?? throw new InvalidOperationException("分享卡片编码失败。");
        return png.ToArray();
    }

    private static SKFont CreateFont(string family, float size, SKFontStyle style)
    {
        return new SKFont(SKTypeface.FromFamilyName(family, style) ?? SKTypeface.Default, size);
    }

    private static SKBitmap LoadAppIcon()
    {
        using var resource = AssetLoader.Open(new Uri(IconResource));
        using var iconStream = new MemoryStream();
        resource.CopyTo(iconStream);
        var icon = iconStream.ToArray();
        if (icon.Length < 62 || icon[2] != 1 || icon[4] == 0)
        {
            throw new InvalidOperationException("BiliCinema 图标格式无效。");
        }

        var dibOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(icon.AsSpan(18, 4)));
        if (dibOffset < 22 || dibOffset > icon.Length - 40)
        {
            throw new InvalidOperationException("BiliCinema 图标数据不完整。");
        }

        var header = icon.AsSpan(dibOffset);
        var headerSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header[..4]));
        var width = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(4, 4));
        var dibHeight = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(8, 4));
        var bitDepth = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(14, 2));
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(16, 4));
        if (headerSize < 40 || width is <= 0 or > 512 || dibHeight != width * 2 ||
            bitDepth != 32 || compression != 0)
        {
            throw new InvalidOperationException("BiliCinema 图标像素格式不受支持。");
        }

        var pixelOffset = checked(dibOffset + headerSize);
        var pixelBytes = checked(width * width * 4);
        if (pixelOffset > icon.Length - pixelBytes)
        {
            throw new InvalidOperationException("BiliCinema 图标像素数据不完整。");
        }

        var bitmap = new SKBitmap(new SKImageInfo(width, width, SKColorType.Bgra8888,
            SKAlphaType.Unpremul));
        for (var y = 0; y < width; y++)
        {
            Marshal.Copy(icon, pixelOffset + (width - 1 - y) * width * 4,
                IntPtr.Add(bitmap.GetPixels(), y * bitmap.RowBytes), width * 4);
        }

        return bitmap;
    }
}
