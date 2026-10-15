using System;
using System.Text.Json.Serialization;

namespace DownKyi.Models;

internal class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("assets")]
    public GitHubReleaseAsset[] Assets { get; set; } = [];

    [JsonPropertyName("published_at")]
    public DateTime? PublishedAt { get; set; }

    public bool IsEmpty()
    {
        return string.IsNullOrEmpty(TagName) &&
               string.IsNullOrEmpty(Name) &&
               string.IsNullOrEmpty(Body) &&
               PublishedAt == null;
    }

    public static bool IsNullOrEmpty(GitHubRelease? release)
    {
        return release == null || release.IsEmpty();
    }
}

internal sealed class GitHubReleaseAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("browser_download_url")]
    public string DownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }
}
