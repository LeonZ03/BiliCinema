using System;
using System.Linq;
using DownKyi.Application.Bilibili;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Presentation;

namespace DownKyi.Services.Watch;

internal sealed record PlaybackTrackSelection(
    string VideoAddress,
    string? AudioAddress,
    int AudioQuality,
    int Quality,
    int Width,
    int Height,
    string VideoCodec,
    string? AudioCodec);

internal static class PlaybackTrackSelector
{
    public static PlaybackTrackSelection Select(VideoPage page, int quality, int codecId, int? audioId)
    {
        ArgumentNullException.ThrowIfNull(page);
        var playUrl = page.PlayUrl ?? throw new InvalidOperationException("请先解析影片媒体。");
        var video = playUrl.Dash.Video.FirstOrDefault(track =>
            track.Id == quality && track.CodecId == codecId);
        if (video == null)
        {
            throw new InvalidOperationException("选择的清晰度或编码没有可播放轨道，请重新选择。");
        }

        var audioTracks = playUrl.Dash.Audio
            .Concat(playUrl.Dash.Dolby?.Audio ?? [])
            .Concat(playUrl.Dash.Flac?.Audio is { } flac ? [flac] : []);
        var audio = audioId is { } selectedAudioId
            ? audioTracks.FirstOrDefault(track => track.Id == selectedAudioId)
            : audioTracks.OrderByDescending(track => track.Id).FirstOrDefault();
        if (audio == null)
        {
            throw new InvalidOperationException("未找到独立音轨，已停止播放以避免无声视频。");
        }

        return new PlaybackTrackSelection(
            NormalizeMediaAddress(video.BaseAddress),
            NormalizeMediaAddress(audio.BaseAddress),
            audio.Id,
            quality,
            video.Width,
            video.Height,
            video.Codecs,
            audio.Codecs);
    }

    public static PlaybackTrackSelection SelectForOnlinePlayback(VideoPage page)
    {
        var codecName = page.VideoQuality.SelectedVideoCodec;
        var codecId = PlaybackQualityCatalog.GetCodecIds()
            .FirstOrDefault(codec => codec.Name == codecName)?.Id
            ?? throw new InvalidOperationException("请选择视频编码。");
        var audioId = PlaybackQualityCatalog.GetAudioQualities()
            .FirstOrDefault(audio => audio.Name == page.AudioQualityFormat)?.Id;
        // The high AAC track of the live test movie stalls on uncached seeks in mpv.
        // Prefer the available medium AAC track for online playback while leaving
        // the download selection and explicit watch-mode track selection untouched.
        if ((audioId is null or 30280)
            && page.PlayUrl?.Dash.Audio.Any(track => track.Id == 30232) == true)
        {
            audioId = 30232;
        }

        return Select(page, page.VideoQuality.Quality, codecId, audioId);
    }

    private static string NormalizeMediaAddress(string address)
    {
        if (!BilibiliResourceAddress.TryNormalizeHttps(address, out var normalized)
            || !Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.Port != 443
            || !IsMediaHost(uri.Host))
        {
            throw new InvalidOperationException("媒体地址不是受信任的 B 站 HTTPS 媒体源。");
        }

        return normalized;
    }

    private static bool IsMediaHost(string host)
    {
        return HasDomain(host, "bilivideo.com")
               || HasDomain(host, "bilivideo.cn")
               || HasDomain(host, "hdslb.com")
               || HasDomain(host, "biliapi.net")
               || HasDomain(host, "bilibili.com");
    }

    private static bool HasDomain(string host, string domain)
    {
        return host.Equals(domain, StringComparison.OrdinalIgnoreCase)
               || host.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase);
    }
}
