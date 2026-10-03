using DownKyi.Application.Diagnostics;
using DownKyi.Core.Aria2cNet.Client;
using DownKyi.Core.Aria2cNet.Client.Entity;
using Microsoft.Extensions.Logging;

namespace DownKyi.Core.Aria2cNet;

public sealed class AriaProgressEventArgs(long totalLength, long completedLength, long speed, string gid) : EventArgs
{
    public long TotalLength { get; } = totalLength;
    public long CompletedLength { get; } = completedLength;
    public long Speed { get; } = speed;
    public string Gid { get; } = gid;
}

public sealed record AriaDownloadStatus(
    DownloadResult Result,
    string? ErrorCode,
    string? ErrorMessage);

public enum AriaDownloadState
{
    Active,
    Waiting,
    Paused,
    Complete,
    Error,
    Removed,
    Unknown
}

public class AriaManager
{
    private const int PollDelayMilliseconds = 500;
    private readonly AriaClient _ariaClient;
    private readonly ILogger<AriaManager> _logger;

    public AriaManager(AriaClient ariaClient, ILogger<AriaManager> logger)
    {
        _ariaClient = ariaClient ?? throw new ArgumentNullException(nameof(ariaClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // gid对应项目的状态
    public event EventHandler<AriaProgressEventArgs>? TellStatus;

    protected virtual void OnTellStatus(long totalLength, long completedLength, long speed, string gid)
    {
        TellStatus?.Invoke(this, new AriaProgressEventArgs(totalLength, completedLength, speed, gid));
    }

    /// <summary>
    /// Gets the download status while preserving aria2's machine-readable failure code.
    /// </summary>
    public async Task<AriaDownloadStatus> GetDownloadStatusDetailAsync(
        string gid,
        Func<CancellationToken, ValueTask>? statusCallback = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gid))
        {
            return new AriaDownloadStatus(
                DownloadResult.FAILED,
                "invalid-gid",
                null);
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var status = await _ariaClient
                .TellStatus(gid, cancellationToken)
                .ConfigureAwait(false);
            if (status?.Result == null)
            {
                if (status?.Error is { } rpcError)
                {
                    var errorCode = rpcError.Message.Contains(
                        "is not found",
                        StringComparison.OrdinalIgnoreCase)
                        ? "not-found"
                        : $"rpc-{rpcError.Code}";
                    return new AriaDownloadStatus(
                        errorCode == "not-found"
                            ? DownloadResult.ABORT
                            : DownloadResult.FAILED,
                        errorCode,
                        rpcError.Message);
                }

                return new AriaDownloadStatus(
                    DownloadResult.FAILED,
                    "rpc-empty",
                    null);
            }

            var result = status.Result;
            var totalLength = ParseLong(result.TotalLength);
            var completedLength = ParseLong(result.CompletedLength);
            var speed = ParseLong(result.DownloadSpeed);

            // 回调
            OnTellStatus(totalLength, completedLength, speed, gid);

            // 在外部执行
            if (statusCallback != null)
            {
                await statusCallback(cancellationToken).ConfigureAwait(false);
            }

            var terminalStatus = await ResolveTerminalStatusAsync(
                gid,
                result,
                cancellationToken).ConfigureAwait(false);
            if (terminalStatus != null)
            {
                return terminalStatus;
            }

            await Task.Delay(PollDelayMilliseconds, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<AriaDownloadStatus?> ResolveTerminalStatusAsync(
        string gid,
        AriaTellStatusResult result,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gid);
        ArgumentNullException.ThrowIfNull(result);
        switch (ClassifyStatus(result))
        {
            case AriaDownloadState.Complete:
                return new AriaDownloadStatus(
                    DownloadResult.SUCCESS,
                    null,
                    null);

            case AriaDownloadState.Removed:
                return new AriaDownloadStatus(
                    DownloadResult.ABORT,
                    "removed",
                    result.ErrorMessage);

            case AriaDownloadState.Error:
                var errorCode = string.IsNullOrEmpty(result.ErrorCode)
                    || result.ErrorCode == "0"
                        ? "unknown-error"
                        : result.ErrorCode;
                _logger.LogErrorMessage(
                    $"aria2 reported a download failure; errorCode={errorCode}.");

                var ariaRemove = await _ariaClient
                    .RemoveDownloadResultAsync(gid, cancellationToken)
                    .ConfigureAwait(false);
                if (ariaRemove?.Result != null)
                {
                    _logger.LogDebugMessage("aria2 removed the failed download result.");
                }

                return new AriaDownloadStatus(
                    DownloadResult.FAILED,
                    errorCode,
                    result.ErrorMessage);

            default:
                return null;
        }
    }

    public static AriaDownloadState ClassifyStatus(AriaTellStatusResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (string.Equals(result.Status, "complete", StringComparison.Ordinal))
        {
            return AriaDownloadState.Complete;
        }

        if (string.Equals(result.Status, "removed", StringComparison.Ordinal))
        {
            return AriaDownloadState.Removed;
        }

        if (string.Equals(result.Status, "error", StringComparison.Ordinal)
            || !string.IsNullOrEmpty(result.ErrorCode) && result.ErrorCode != "0")
        {
            return AriaDownloadState.Error;
        }

        return result.Status switch
        {
            "active" => AriaDownloadState.Active,
            "waiting" => AriaDownloadState.Waiting,
            "paused" => AriaDownloadState.Paused,
            _ => AriaDownloadState.Unknown
        };
    }

    private static long ParseLong(string? value)
    {
        return long.TryParse(value, out var parsed) ? parsed : 0;
    }
}
