using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DownKyi.Core.FFmpeg;

internal sealed record FfmpegMediaValidationResult(
    bool IsValid,
    TimeSpan Duration,
    string? FailureReason)
{
    public static FfmpegMediaValidationResult Failure(string reason)
    {
        return new FfmpegMediaValidationResult(false, TimeSpan.Zero, reason);
    }
}

internal interface IFfmpegMediaValidator
{
    Task<FfmpegMediaValidationResult> ValidateAsync(
        string mediaFile,
        TimeSpan expectedDuration,
        CancellationToken cancellationToken = default);
}

internal sealed class FfmpegMediaValidator : IFfmpegMediaValidator
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TailSeekSafetyOffset = TimeSpan.FromSeconds(1);
    private readonly IFfmpegProcessRunner _processRunner;

    public FfmpegMediaValidator(IFfmpegProcessRunner processRunner)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    public async Task<FfmpegMediaValidationResult> ValidateAsync(
        string mediaFile,
        TimeSpan expectedDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaFile);
        if (!File.Exists(mediaFile) || new FileInfo(mediaFile).Length == 0)
        {
            return FfmpegMediaValidationResult.Failure("Output file is missing or empty.");
        }

        if (expectedDuration <= TimeSpan.Zero)
        {
            return FfmpegMediaValidationResult.Failure("Expected source duration is missing or invalid.");
        }

        var (document, probeFailure) = await ProbeAsync(mediaFile, cancellationToken)
            .ConfigureAwait(false);
        if (probeFailure != null)
        {
            return FfmpegMediaValidationResult.Failure(probeFailure);
        }

        if (!HasStream(document, "video"))
        {
            return FfmpegMediaValidationResult.Failure("Output has no video stream.");
        }

        if (!double.TryParse(
                document!.Format?.Duration,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var durationSeconds) ||
            !double.IsFinite(durationSeconds) ||
            durationSeconds <= 0)
        {
            return FfmpegMediaValidationResult.Failure("Output duration is missing or invalid.");
        }

        var duration = TimeSpan.FromSeconds(durationSeconds);
        foreach (var position in GetSeekPositions(expectedDuration))
        {
            var decode = await _processRunner
                .RunAsync(FfmpegCommandFactory.BuildSeekDecode(mediaFile, position), ProcessTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (!decode.Succeeded || !DecodedAtLeastOneFrame(decode.StandardOutput))
            {
                return FfmpegMediaValidationResult.Failure("Output cannot decode after seeking.");
            }
        }

        return new FfmpegMediaValidationResult(true, duration, null);
    }

    public async Task<bool> ValidateRequiredStreamsAsync(
        string mediaFile,
        bool requireAudio,
        bool requireVideo,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaFile);
        if (!File.Exists(mediaFile) || new FileInfo(mediaFile).Length == 0)
        {
            return false;
        }

        if (requireAudio && !await CanDecodeRequiredStreamAsync(
                mediaFile,
                FfmpegMediaStreamKind.Audio,
                cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        return !requireVideo || await CanDecodeRequiredStreamAsync(
                mediaFile,
                FfmpegMediaStreamKind.Video,
                cancellationToken).ConfigureAwait(false);
    }

    internal static IReadOnlyList<TimeSpan> GetSeekPositions(TimeSpan expectedDuration)
    {
        var middle = TimeSpan.FromTicks(expectedDuration.Ticks / 2);
        var tail = expectedDuration > TailSeekSafetyOffset
            ? expectedDuration - TailSeekSafetyOffset
            : TimeSpan.Zero;
        return [TimeSpan.Zero, middle, tail];
    }

    private static bool DecodedAtLeastOneFrame(string progressOutput)
    {
        foreach (var line in progressOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("frame=", StringComparison.Ordinal) &&
                int.TryParse(line.AsSpan("frame=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var frames) &&
                frames > 0)
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> CanDecodeRequiredStreamAsync(
        string mediaFile,
        FfmpegMediaStreamKind streamKind,
        CancellationToken cancellationToken)
    {
        var decode = await _processRunner
            .RunAsync(
                FfmpegCommandFactory.BuildRequiredStreamDecode(mediaFile, streamKind),
                ProcessTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        return decode.Succeeded && DecodedPositiveDuration(decode.StandardOutput);
    }

    private static bool DecodedPositiveDuration(string progressOutput)
    {
        foreach (var line in progressOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                long.TryParse(
                    line.AsSpan("out_time_us=".Length),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var microseconds) &&
                microseconds > 0)
            {
                return true;
            }
        }

        return false;
    }

    private async Task<(FfprobeDocument? Document, string? FailureReason)> ProbeAsync(
        string mediaFile,
        CancellationToken cancellationToken)
    {
        var probe = await _processRunner
            .RunAsync(FfmpegCommandFactory.BuildProbe(mediaFile), ProcessTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (!probe.Succeeded)
        {
            return (null, "ffprobe could not read the output.");
        }

        try
        {
            return (
                JsonSerializer.Deserialize(
                    probe.StandardOutput,
                    FfprobeJsonContext.Default.FfprobeDocument),
                null);
        }
        catch (JsonException)
        {
            return (null, "ffprobe returned malformed JSON.");
        }
    }

    private static bool HasStream(FfprobeDocument? document, string codecType) =>
        document?.Streams?.Any(stream => string.Equals(
            stream.CodecType,
            codecType,
            StringComparison.OrdinalIgnoreCase)) == true;
}

internal sealed class FfprobeDocument
{
    [JsonPropertyName("streams")]
    public IReadOnlyList<FfprobeStream>? Streams { get; init; }

    [JsonPropertyName("format")]
    public FfprobeFormat? Format { get; init; }
}

internal sealed class FfprobeStream
{
    [JsonPropertyName("codec_type")]
    public string? CodecType { get; init; }
}

internal sealed class FfprobeFormat
{
    [JsonPropertyName("duration")]
    public string? Duration { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified)]
[JsonSerializable(typeof(FfprobeDocument))]
internal sealed partial class FfprobeJsonContext : JsonSerializerContext;
