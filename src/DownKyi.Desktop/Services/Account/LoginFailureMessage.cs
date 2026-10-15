using System;
using DownKyi.Application.Bilibili;

namespace DownKyi.Services.Account;

internal static class LoginFailureMessage
{
    internal static string FromException(Exception exception)
    {
        var kind = exception is BilibiliHttpRequestException failure
            ? failure.FailureKind
            : exception is NotSupportedException
                ? BilibiliHttpFailureKind.UnsupportedProtocol
                : exception is OperationCanceledException
                    ? BilibiliHttpFailureKind.Timeout
                    : BilibiliHttpFailureKind.Transport;
        return kind switch
        {
            BilibiliHttpFailureKind.UnsupportedProtocol => "当前网络或代理不支持所需协议，请更换网络或检查代理设置后重试登录。",
            BilibiliHttpFailureKind.Proxy => "无法连接代理服务器，请检查代理地址和协议后重试登录。",
            BilibiliHttpFailureKind.SecureConnection => "无法建立安全连接，请检查系统时间、证书和代理设置后重试登录。",
            BilibiliHttpFailureKind.NameResolution => "无法解析哔哩哔哩服务器地址，请检查网络和 DNS 后重试登录。",
            BilibiliHttpFailureKind.Timeout => "登录请求超时，请检查网络后重试。",
            BilibiliHttpFailureKind.RateLimited => "登录请求过于频繁，请稍后重试。",
            _ => "登录连接失败，请检查网络和代理设置后重试。"
        };
    }
}
