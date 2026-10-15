using System.Net.Sockets;
using System.Security.Authentication;
using DownKyi.Application.Bilibili;

namespace DownKyi.Infrastructure.Bilibili;

internal static class BilibiliNetworkFailure
{
    internal static bool IsTransportException(Exception exception) =>
        exception is HttpRequestException or IOException or InvalidOperationException
            or NotSupportedException or AuthenticationException or SocketException
            or ArgumentException;

    internal static BilibiliHttpRequestException Wrap(Exception exception)
    {
        if (exception is BilibiliHttpRequestException typed)
        {
            return typed;
        }

        var kind = Classify(exception);
        return new BilibiliHttpRequestException(
            "The Bilibili network request could not be completed.",
            kind,
            innerException: exception);
    }

    private static BilibiliHttpFailureKind Classify(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            switch (current)
            {
                case OperationCanceledException or TimeoutException:
                    return BilibiliHttpFailureKind.Timeout;
                case NotSupportedException:
                    return BilibiliHttpFailureKind.UnsupportedProtocol;
                case AuthenticationException:
                    return BilibiliHttpFailureKind.SecureConnection;
                case SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain }:
                    return BilibiliHttpFailureKind.NameResolution;
                case HttpRequestException http:
                    var kind = http.HttpRequestError switch
                    {
                        HttpRequestError.NameResolutionError => BilibiliHttpFailureKind.NameResolution,
                        HttpRequestError.SecureConnectionError => BilibiliHttpFailureKind.SecureConnection,
                        HttpRequestError.ProxyTunnelError => BilibiliHttpFailureKind.Proxy,
                        HttpRequestError.HttpProtocolError or HttpRequestError.ExtendedConnectNotSupported
                            or HttpRequestError.VersionNegotiationError => BilibiliHttpFailureKind.UnsupportedProtocol,
                        _ => BilibiliHttpFailureKind.Transport
                    };
                    if (kind != BilibiliHttpFailureKind.Transport)
                    {
                        return kind;
                    }

                    break;
            }
        }

        return BilibiliHttpFailureKind.Transport;
    }
}
