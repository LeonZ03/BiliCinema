using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DownKyi.Services.Watch;

internal sealed class MpvPlaybackSession : IDisposable
{
    private readonly Process _process;
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private int _requestId;
    private bool _disposed;
    private double _lastKnownPosition;

    private MpvPlaybackSession(Process process, NamedPipeClientStream pipe)
    {
        _process = process;
        _pipe = pipe;
        _reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        _writer = CreateCommandWriter(pipe);
    }

    internal static StreamWriter CreateCommandWriter(Stream stream)
    {
        // mpv treats a UTF-8 BOM before the first JSON object as an invalid command.
        return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            leaveOpen: true)
        {
            AutoFlush = true
        };
    }

    public bool HasExited => _process.HasExited;

    public static async Task<MpvPlaybackSession> StartAsync(
        PlaybackTrackSelection tracks,
        string userAgent,
        CancellationToken cancellationToken,
        bool startPaused = false)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        var executable = FindMpv() ?? throw new InvalidOperationException(
            "找不到 mpv 播放器。请安装 mpv，或设置 DOWNKYI_MPV_PATH 为 mpv.exe 的完整路径。");
        var pipeName = $"downkyi-watch-{Guid.NewGuid():N}";
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = false
        };
        foreach (var argument in new[]
                 {
                     "--no-config", "--no-ytdl", "--cookies=no", "--cache=yes",
                     "--cache-on-disk=no", "--demuxer-max-bytes=192MiB",
                     "--demuxer-max-back-bytes=16MiB", "--cache-secs=60",
                     "--cache-pause-initial=yes", "--cache-pause-wait=5",
                     "--keep-open=no", "--idle=no",
                     "--terminal=no", "--force-window=yes", "--referrer=https://www.bilibili.com/",
                     startPaused ? "--pause=yes" : "--pause=no",
                     $"--user-agent={userAgent}", $"--input-ipc-server={pipeName}"
                 })
        {
            start.ArgumentList.Add(argument);
        }

        if (tracks.AudioAddress != null)
        {
            start.ArgumentList.Add($"--audio-file={tracks.AudioAddress}");
        }

        start.ArgumentList.Add(tracks.VideoAddress);

        var process = Process.Start(start) ?? throw new InvalidOperationException("mpv 播放器无法启动。");
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        MpvPlaybackSession? session = null;
        var transferred = false;
        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(TimeSpan.FromSeconds(30));
            await pipe.ConnectAsync(startup.Token).ConfigureAwait(false);
            session = new MpvPlaybackSession(process, pipe);
            await session.WaitForMediaAsync(startup.Token).ConfigureAwait(false);
            var ready = session;
            session = null;
            transferred = true;
            return ready;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is OperationCanceledException or IOException
            or InvalidOperationException)
        {
            var reason = process.HasExited
                ? $"播放器进程提前退出（退出码 {process.ExitCode}）。"
                : "媒体打开或本机控制连接超时。";
            throw new InvalidOperationException($"mpv 无法开始播放：{reason}", error);
        }
        finally
        {
            session?.Dispose();
            if (!transferred && session == null)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                process.Dispose();
            }
        }
    }

    public async Task<double> GetPositionAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var data = await SendAsync(["get_property", "time-pos"], cancellationToken)
                .ConfigureAwait(false);
            if (data.RootElement.ValueKind == JsonValueKind.Number)
            {
                _lastKnownPosition = data.RootElement.GetDouble();
            }

            return _lastKnownPosition;
        }
        catch (MpvIpcCommandException error) when (error.Code == "property unavailable" && !_process.HasExited)
        {
            // Metadata is available before the first decoded frame and time position.
            return _lastKnownPosition;
        }
    }

    public async Task<bool> GetPausedAsync(CancellationToken cancellationToken)
    {
        using var data = await SendAsync(["get_property", "pause"], cancellationToken)
            .ConfigureAwait(false);
        return data.RootElement.ValueKind == JsonValueKind.True;
    }

    public async Task<bool> GetBufferingAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var data = await SendAsync(["get_property", "paused-for-cache"], cancellationToken)
                .ConfigureAwait(false);
            return data.RootElement.ValueKind == JsonValueKind.True;
        }
        catch (MpvIpcCommandException error) when (error.Code == "property unavailable" && !_process.HasExited)
        {
            return false;
        }
    }

    public async Task<(int Width, int Height)?> GetDecodedDimensionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var width = await SendAsync(["get_property", "video-params/w"], cancellationToken)
                .ConfigureAwait(false);
            using var height = await SendAsync(["get_property", "video-params/h"], cancellationToken)
                .ConfigureAwait(false);
            if (width.RootElement.TryGetInt32(out var decodedWidth)
                && height.RootElement.TryGetInt32(out var decodedHeight))
            {
                return (decodedWidth, decodedHeight);
            }
        }
        catch (InvalidOperationException) when (!_process.HasExited)
        {
            // The first frame may not have been decoded yet.
        }

        return null;
    }

    private async Task WaitForMediaAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var data = await SendAsync(["get_property", "duration"], cancellationToken)
                    .ConfigureAwait(false);
                if (data.RootElement.ValueKind == JsonValueKind.Number)
                {
                    return;
                }
            }
            catch (InvalidOperationException) when (!_process.HasExited)
            {
                // mpv has not finished opening the media yet.
            }

            try
            {
                using var width = await SendAsync(["get_property", "video-params/w"], cancellationToken)
                    .ConfigureAwait(false);
                if (width.RootElement.ValueKind == JsonValueKind.Number)
                {
                    return;
                }
            }
            catch (InvalidOperationException) when (!_process.HasExited)
            {
                // Some streams have no known duration before decoding begins.
            }

            if (_process.HasExited)
            {
                throw new InvalidOperationException("播放器无法打开所选音视频轨道，请检查网络或尝试兼容编码。");
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task SeekAsync(double seconds, CancellationToken cancellationToken)
    {
        try
        {
            using var _ = await SendAsync(["seek", Math.Max(0, seconds), "absolute"], cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MpvIpcCommandException error) when (error.Code == "property unavailable")
        {
            throw new InvalidOperationException("媒体仍在载入，请稍后重试跳转。", error);
        }
    }

    public async Task SetPausedAsync(bool paused, CancellationToken cancellationToken)
    {
        using var _ = await SendAsync(["set_property", "pause", paused], cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SetSpeedAsync(double speed, CancellationToken cancellationToken)
    {
        using var _ = await SendAsync(["set_property", "speed", speed], cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<JsonDocument> SendAsync(object?[] command, CancellationToken cancellationToken)
    {
        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException("播放器已退出。");
            }

            var id = ++_requestId;
            var request = JsonSerializer.Serialize(new { command, request_id = id });
            await _writer.WriteLineAsync(request.AsMemory(), cancellationToken).ConfigureAwait(false);
            while (true)
            {
                var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
                           ?? throw new IOException("播放器控制连接已关闭。");
                using var response = JsonDocument.Parse(line);
                var root = response.RootElement;
                if (!root.TryGetProperty("request_id", out var responseId)
                    || responseId.GetInt32() != id)
                {
                    continue;
                }

                if (!root.TryGetProperty("error", out var error)
                    || error.GetString() != "success")
                {
                    throw new MpvIpcCommandException(error.ValueKind == JsonValueKind.String
                        ? error.GetString() ?? "unknown"
                        : "unknown");
                }

                return JsonDocument.Parse(root.TryGetProperty("data", out var data)
                    ? data.GetRawText()
                    : "null");
            }
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private static string? FindMpv()
    {
        var configured = Environment.GetEnvironmentVariable("DOWNKYI_MPV_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured) ? Path.GetFullPath(configured) : null;
        }

        var bundled = Path.Combine(AppContext.BaseDirectory, "mpv", "mpv.exe");
        if (File.Exists(bundled))
        {
            return bundled;
        }

        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(folder.Trim('"'), "mpv.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // StreamWriter flushes during disposal, so the pipe must stay open until both
        // wrappers have been released. A failed media open can already have closed it.
        try
        {
            _writer.Dispose();
        }
        catch (IOException)
        {
            // mpv closed its end before the final buffered write was flushed.
        }
        catch (ObjectDisposedException)
        {
            // The pipe was closed concurrently with session teardown.
        }

        _reader.Dispose();
        _pipe.Dispose();
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // mpv exited between the state check and termination.
        }

        _commandGate.Dispose();
        _process.Dispose();
    }
}

internal sealed class MpvIpcCommandException : InvalidOperationException
{
    public MpvIpcCommandException() : this("unknown")
    {
    }

    public MpvIpcCommandException(string code) : base($"播放器命令失败（{code}）。")
    {
        Code = code;
    }

    public MpvIpcCommandException(string code, Exception innerException)
        : base($"播放器命令失败（{code}）。", innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
