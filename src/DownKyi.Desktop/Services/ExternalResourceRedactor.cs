using System.Text.RegularExpressions;

namespace DownKyi.Services;

internal static partial class ExternalResourceRedactor
{
    public static string Redact(string? text)
    {
        return ExternalResourceLineRegex().Replace(
            text ?? string.Empty,
            "$1[resource redacted]");
    }

    [GeneratedRegex(
        "(^|[^\\w])(?:[a-z][a-z0-9+.-]*://|[a-z]:[\\\\/]|[\\\\/])[^\\r\\n]*",
        RegexOptions.IgnoreCase |
        RegexOptions.Multiline |
        RegexOptions.CultureInvariant |
        RegexOptions.NonBacktracking)]
    private static partial Regex ExternalResourceLineRegex();
}
