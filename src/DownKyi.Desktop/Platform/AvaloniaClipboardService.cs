using System.Threading;
using System.Threading.Tasks;
using System.IO;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using DownKyi.Application.Desktop;

namespace DownKyi.Platform;

internal sealed class AvaloniaClipboardService : IClipboardService
{
    private readonly AvaloniaDesktopContext _desktopContext;

    public AvaloniaClipboardService(AvaloniaDesktopContext desktopContext)
    {
        _desktopContext = desktopContext ?? throw new System.ArgumentNullException(nameof(desktopContext));
    }

    public async Task SetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var clipboard = _desktopContext.MainWindow.Clipboard;
        if (clipboard != null)
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
        }
    }

    public async Task SetPngImageAsync(byte[] imagePng, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new MemoryStream(imagePng, writable: false);
        using var bitmap = new Bitmap(stream);
        using var transfer = new DataTransfer();
        var item = new DataTransferItem();
        item.SetBitmap(bitmap);
        transfer.Add(item);
        var clipboard = _desktopContext.MainWindow.Clipboard
                        ?? throw new IOException("剪贴板不可用。");
        await clipboard.SetDataAsync(transfer).ConfigureAwait(true);
        await clipboard.FlushAsync().ConfigureAwait(true);
    }
}
