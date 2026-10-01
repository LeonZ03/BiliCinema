using System.Web;
using DownKyi.Application.Bilibili;

namespace DownKyi.Infrastructure.Bilibili;

internal sealed class BilibiliApiClient : IBilibiliApiClient
{
    private readonly BilibiliHttpTransport _transport;
    private readonly IBilibiliCookieProvider _cookieProvider;
    private readonly IBuvidProvider _buvidProvider;

    public BilibiliApiClient(
        BilibiliHttpTransport transport,
        IBilibiliCookieProvider cookieProvider,
        IBuvidProvider buvidProvider)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _cookieProvider = cookieProvider
                          ?? throw new ArgumentNullException(nameof(cookieProvider));
        _buvidProvider = buvidProvider ?? throw new ArgumentNullException(nameof(buvidProvider));
    }

    public async Task<string> GetStringAsync(
        BilibiliHttpRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var credentials = await GetCredentialsAsync(request, cancellationToken)
            .ConfigureAwait(false);
        return await _transport.GetStringAsync(
            () => BuildRequest(request, credentials),
            request.Attempts,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Stream> OpenReadAsync(
        BilibiliHttpRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var credentials = await GetCredentialsAsync(request, cancellationToken)
            .ConfigureAwait(false);
        return await _transport.OpenReadAsync(
            () => BuildRequest(request, credentials),
            request.Attempts,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task DownloadFileAsync(
        BilibiliHttpRequest request,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var temporaryPath = $"{destinationPath}.download";
        try
        {
            var input = await OpenReadAsync(request, cancellationToken).ConfigureAwait(false);
            await using (input.ConfigureAwait(false))
            {
                var output = new FileStream(
                    temporaryPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using (output.ConfigureAwait(false))
                {
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        catch
        {
            DeleteTemporaryFileBestEffort(temporaryPath);
            throw;
        }
    }

    private async Task<string> GetCredentialsAsync(
        BilibiliHttpRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.IncludeCredentials)
        {
            return string.Empty;
        }

        var cookieHeader = _cookieProvider.GetCookieHeader();
        if (!request.IncludeBuvid)
        {
            return cookieHeader;
        }

        var buvid = await _buvidProvider.GetAsync(cancellationToken).ConfigureAwait(false);
        return SetCookie(
            SetCookie(cookieHeader, "buvid3", buvid.Buvid3),
            "buvid4",
            buvid.Buvid4);
    }

    private static HttpRequestMessage BuildRequest(
        BilibiliHttpRequest request,
        string credentials)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, request.RequestAddress);
        if (request.Referer != null)
        {
            message.Headers.Referrer = new Uri(request.Referer);
        }

        if (request.IncludeCredentials)
        {
            message.Headers.Add("origin", "https://www.bilibili.com");
            if (!string.IsNullOrWhiteSpace(credentials))
            {
                message.Headers.Add("cookie", credentials);
            }
        }

        return message;
    }

    private static string SetCookie(string cookieHeader, string name, string value)
    {
        var encoded = HttpUtility.UrlEncode(value);
        var cookies = cookieHeader
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(cookie => !HasName(cookie, name))
            .Append($"{name}={encoded}");
        return string.Join("; ", cookies);
    }

    private static bool HasName(string cookie, string name)
    {
        var separator = cookie.IndexOf('=', StringComparison.Ordinal);
        return separator > 0
               && cookie.AsSpan(0, separator).Trim().Equals(
                   name,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static void DeleteTemporaryFileBestEffort(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
    }
}
