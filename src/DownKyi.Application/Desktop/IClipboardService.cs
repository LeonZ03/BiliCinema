namespace DownKyi.Application.Desktop;

public interface IClipboardService
{
    Task SetTextAsync(string text, CancellationToken cancellationToken = default);

    Task SetPngImageAsync(byte[] imagePng, CancellationToken cancellationToken = default);
}
