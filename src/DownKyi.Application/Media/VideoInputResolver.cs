using System.Text.RegularExpressions;

namespace DownKyi.Application.Media;

public static partial class VideoInputResolver
{
    [GeneratedRegex("""https?://[^\s<>"'“”‘’\[\]（）()「」『』《》【】、，。；：！？…]+""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex UrlCandidate();

    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var value = input.Trim()
            .Replace("\\:", ":", StringComparison.Ordinal)
            .Replace("\\.", ".", StringComparison.Ordinal)
            .Replace("\\/", "/", StringComparison.Ordinal);
        var match = UrlCandidate().Match(value);
        if (match.Success)
        {
            var candidate = match.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '，', '。', '；', '：', '！', '？', '）', '】', '》');
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && IsSafeTrustedUri(uri) &&
                ResolveUri(uri) != VideoInputKind.Unknown)
            {
                return candidate;
            }
        }

        return value;
    }

    public static VideoInputKind Resolve(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return VideoInputKind.Unknown;
        }

        var value = Normalize(input);
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return IsSafeTrustedUri(uri) ? ResolveUri(uri) : VideoInputKind.Unknown;
        }

        if (value.Contains("://", StringComparison.Ordinal) || UrlCandidate().IsMatch(value))
        {
            return VideoInputKind.Unknown;
        }

        return ResolveIdentifier(value);
    }

    private static VideoInputKind ResolveUri(Uri uri)
    {
        var value = uri.AbsolutePath;
        if (uri.Host.Equals("b23.tv", StringComparison.OrdinalIgnoreCase))
        {
            if (Regex.IsMatch(value, @"/(?:BV[a-zA-Z0-9]+|av\d+)(?:/|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                return VideoInputKind.Video;
            }
            if (Regex.IsMatch(value, @"/ep\d+(?:/|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                return VideoInputKind.Bangumi;
            }
            if (Regex.IsMatch(value, @"/ss\d+(?:/|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                return VideoInputKind.Bangumi;
            }
            return VideoInputKind.Unknown;
        }

        return ResolveIdentifier(value);
    }

    private static VideoInputKind ResolveIdentifier(string value)
    {
        if (ContainsPath(value, "/cheese/play/") || StartsWithId(value, "cheese"))
        {
            return VideoInputKind.Cheese;
        }

        if (StartsWithId(value, "ss") ||
            StartsWithId(value, "ep") ||
            StartsWithId(value, "md") ||
            ContainsPath(value, "/bangumi/play/") ||
            ContainsPath(value, "/bangumi/media/"))
        {
            return VideoInputKind.Bangumi;
        }

        if (StartsWithId(value, "av") ||
            value.StartsWith("BV", StringComparison.OrdinalIgnoreCase) ||
            ContainsPath(value, "/video/"))
        {
            return VideoInputKind.Video;
        }

        return VideoInputKind.Unknown;
    }

    private static bool IsSafeTrustedUri(Uri uri)
    {
        return (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) &&
            string.IsNullOrEmpty(uri.UserInfo) &&
            (uri.Host.Equals("bilibili.com", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.Equals("b23.tv", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsPath(string input, string path)
    {
        return input.Contains(path, StringComparison.OrdinalIgnoreCase);
    }

    private static bool StartsWithId(string input, string prefix)
    {
        if (!input.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || input.Length == prefix.Length)
        {
            return false;
        }

        return input.AsSpan(prefix.Length).IndexOfAnyExceptInRange('0', '9') < 0;
    }
}
