using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.Login;
using DownKyi.Core.Settings;
using DownKyi.Presentation;
using DownKyi.Services.Account;
using DownKyi.Services.Video;
using DownKyi.Services.Watch;

namespace DownKyi.ViewModels;

internal sealed class WatchWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ILoginCoordinator _login;
    private readonly ILoginQrCodeRenderer _qrRenderer;
    private readonly IUserSessionCoordinator _session;
    private readonly ISettingsStore _settings;
    private readonly IClipboardService _clipboard;
    private readonly VideoParseCoordinator _parser;
    private readonly WatchRoomClient _room = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _snapshotGate = new(1, 1);
    private readonly Task _monitorTask;
    private CancellationTokenSource? _loginCancellation;
    private MpvPlaybackSession? _player;
    private VideoPage? _page;
    private WatchRoomSnapshot? _lastSnapshot;
    private long _lastSnapshotReceivedAtUnixMs;
    private long _lastVersion = -1;
    private double _lastPosition;
    private long? _resumeEpisodeId;
    private double _resumeAtSeconds;
    private bool _seekDragging;
    private bool _lastBuffering;
    private bool _startWhenReady;
    private int _recoveries;
    private bool _disposed;

    private string _status = "未登录。请扫码登录。";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    private bool _isLoggedIn;
    public bool IsLoggedIn { get => _isLoggedIn; private set => SetProperty(ref _isLoggedIn, value); }

    private Bitmap? _loginQrCode;
    public Bitmap? LoginQrCode { get => _loginQrCode; private set => SetProperty(ref _loginQrCode, value); }

    private string _videoInput = string.Empty;
    public string VideoInput { get => _videoInput; set => SetProperty(ref _videoInput, value); }

    private string _filmTitle = string.Empty;
    public string FilmTitle { get => _filmTitle; private set => SetProperty(ref _filmTitle, value); }

    private string _mediaInfo = string.Empty;
    public string MediaInfo { get => _mediaInfo; private set => SetProperty(ref _mediaInfo, value); }

    private string _positionText = "00:00:00";
    public string PositionText { get => _positionText; private set => SetProperty(ref _positionText, value); }

    private string _seekSeconds = "0";
    public string SeekSeconds { get => _seekSeconds; set => SetProperty(ref _seekSeconds, value); }

    private double _durationSeconds = 1;
    public double DurationSeconds { get => _durationSeconds; private set => SetProperty(ref _durationSeconds, value); }

    private double _seekPositionSeconds;
    public double SeekPositionSeconds { get => _seekPositionSeconds; set => SetProperty(ref _seekPositionSeconds, value); }

    private string _rateText = "1.0";
    public string RateText { get => _rateText; set => SetProperty(ref _rateText, value); }

    private string _serviceAddress = "ws://127.0.0.1:5077/ws";
    public string ServiceAddress { get => _serviceAddress; set => SetProperty(ref _serviceAddress, value); }

    private string _inviteText = string.Empty;
    public string InviteText { get => _inviteText; set => SetProperty(ref _inviteText, value); }

    private string _roomState = "未加入房间";
    public string RoomState { get => _roomState; private set => SetProperty(ref _roomState, value); }

    private string _syncStatus = string.Empty;
    public string SyncStatus { get => _syncStatus; private set => SetProperty(ref _syncStatus, value); }

    private WatchQualityOption? _selectedQuality;
    public WatchQualityOption? SelectedQuality { get => _selectedQuality; set => SetProperty(ref _selectedQuality, value); }

    private WatchAudioOption? _selectedAudio;
    public WatchAudioOption? SelectedAudio { get => _selectedAudio; set => SetProperty(ref _selectedAudio, value); }

    public ObservableCollection<WatchQualityOption> QualityOptions { get; } = [];
    public ObservableCollection<WatchAudioOption> AudioOptions { get; } = [];
    public bool CanControl => !_room.Connected || _room.IsHost;

    public IAsyncRelayCommand LoginCommand { get; }
    public IAsyncRelayCommand LogoutCommand { get; }
    public IAsyncRelayCommand ParseCommand { get; }
    public IAsyncRelayCommand PlayCommand { get; }
    public IAsyncRelayCommand PauseCommand { get; }
    public IAsyncRelayCommand SeekCommand { get; }
    public IAsyncRelayCommand RateCommand { get; }
    public IAsyncRelayCommand CreateRoomCommand { get; }
    public IAsyncRelayCommand JoinRoomCommand { get; }
    public IAsyncRelayCommand CopyInviteCommand { get; }
    public IAsyncRelayCommand LeaveRoomCommand { get; }
    public IAsyncRelayCommand EndRoomCommand { get; }
    public IAsyncRelayCommand StopCommand { get; }

    public WatchWindowViewModel(
        ILoginCoordinator login,
        ILoginQrCodeRenderer qrRenderer,
        IUserSessionCoordinator session,
        ISettingsStore settings,
        IClipboardService clipboard,
        VideoParseCoordinator parser)
    {
        _login = login;
        _qrRenderer = qrRenderer;
        _session = session;
        _settings = settings;
        _clipboard = clipboard;
        _parser = parser;
        _room.SnapshotReceived += OnSnapshotReceived;
        _room.Disconnected += OnRoomDisconnected;
        _room.Closed += OnRoomClosed;
        LoginCommand = new AsyncRelayCommand(StartLoginAsync);
        LogoutCommand = new AsyncRelayCommand(LogoutAsync);
        ParseCommand = new AsyncRelayCommand(ParseAsync);
        PlayCommand = new AsyncRelayCommand(() => RunControlSafelyAsync(PlayAsync));
        PauseCommand = new AsyncRelayCommand(() => RunControlSafelyAsync(PauseAsync));
        SeekCommand = new AsyncRelayCommand(() => RunControlSafelyAsync(SeekAsync));
        RateCommand = new AsyncRelayCommand(() => RunControlSafelyAsync(SetRateAsync));
        CreateRoomCommand = new AsyncRelayCommand(CreateRoomAsync);
        JoinRoomCommand = new AsyncRelayCommand(JoinRoomAsync);
        CopyInviteCommand = new AsyncRelayCommand(CopyInviteAsync);
        LeaveRoomCommand = new AsyncRelayCommand(LeaveRoomAsync);
        EndRoomCommand = new AsyncRelayCommand(EndRoomAsync);
        StopCommand = new AsyncRelayCommand(StopAsync);
        _monitorTask = MonitorPlaybackAsync(_lifetime.Token);
    }

    public async Task InitializeAsync()
    {
        try
        {
            var snapshot = await _session.RefreshAsync(_lifetime.Token).ConfigureAwait(true);
            IsLoggedIn = snapshot.UserInfo?.IsLogin == true;
            Status = IsLoggedIn ? "已登录。粘贴影片链接并解析。" : "未登录。请扫码登录。";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception error) when (IsRoutineError(error))
        {
            Status = "登录状态检查失败，请检查网络后重试扫码。";
        }
    }

    private async Task StartLoginAsync()
    {
        if (_loginCancellation != null)
        {
            await _loginCancellation.CancelAsync().ConfigureAwait(true);
        }
        _loginCancellation?.Dispose();
        _loginCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var cancellationToken = _loginCancellation.Token;
        try
        {
            Status = "正在生成登录二维码…";
            var loginUrl = await _login.RequestLoginUrlAsync(cancellationToken).ConfigureAwait(true);
            if (loginUrl?.Code != 0 || loginUrl.Data?.QrCodeAddress == null
                || loginUrl.Data.QrcodeKey == null)
            {
                Status = "二维码生成失败，请重试。";
                return;
            }

            var uri = new Uri(loginUrl.Data.QrCodeAddress, UriKind.Absolute);
            LoginQrCode?.Dispose();
            LoginQrCode = _qrRenderer.Render(uri);
            Status = "请用哔哩哔哩 App 扫码并确认。";
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(1000, cancellationToken).ConfigureAwait(true);
                var result = await _login.GetLoginStatusAsync(
                    loginUrl.Data.QrcodeKey, cancellationToken).ConfigureAwait(true);
                var data = result?.Status.Data;
                if (data == null || data.Code is 86101 or 86090)
                {
                    continue;
                }

                if (data.Code == 86038)
                {
                    Status = "二维码已过期，请重新获取。";
                    return;
                }

                if (data.Code == 0)
                {
                    var redirect = new Uri(data.RedirectAddress, UriKind.Absolute);
                    if (!await _login.SaveLoginCookiesAsync(result!, redirect, cancellationToken)
                            .ConfigureAwait(true))
                    {
                        Status = "登录验证失败，请重新扫码。";
                        return;
                    }

                    await InitializeAsync().ConfigureAwait(true);
                    LoginQrCode?.Dispose();
                    LoginQrCode = null;
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception error) when (IsRoutineError(error))
        {
            Status = "扫码登录失败，请检查网络并重试。";
        }
    }

    private async Task LogoutAsync()
    {
        await LeaveRoomAsync().ConfigureAwait(true);
        StopPlayback();
        if (_loginCancellation != null)
        {
            await _loginCancellation.CancelAsync().ConfigureAwait(true);
        }
        IsLoggedIn = false;
        if (!LoginHelper.DeleteLoginInfoCookies())
        {
            Status = "本机登录信息清理失败，请检查数据目录权限。";
            return;
        }

        await InitializeAsync().ConfigureAwait(true);
        Status = "已退出本机 B 站账号。";
    }

    private Task ParseAsync() => ParseInputAsync(VideoInput, _lifetime.Token);

    private async Task ParseInputAsync(string input, CancellationToken cancellationToken)
    {
        if (!IsLoggedIn)
        {
            Status = "请先扫码登录。";
            return;
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            Status = "请输入影片链接。";
            return;
        }

        try
        {
            StopPlayback();
            Status = "正在解析影片…";
            var detail = await _parser.LoadDetailAsync(input.Trim(), refresh: true, cancellationToken)
                .ConfigureAwait(true);
            var pages = detail.VideoSections.SelectMany(section => section.VideoPages).ToArray();
            var episodeId = ParseEntrance.GetBangumiEpisodeId(input);
            var page = pages.FirstOrDefault(candidate => candidate.EpisodeId == episodeId)
                       ?? pages.FirstOrDefault();
            if (page == null)
            {
                Status = "没有找到可播放剧集，请检查影片链接。";
                return;
            }

            var stream = await _parser.LoadPageStreamAsync(input.Trim(), page, refresh: false,
                cancellationToken).ConfigureAwait(true);
            if (stream?.PlayUrl == null)
            {
                Status = "媒体解析失败，请检查登录状态或影片权限。";
                return;
            }

            page.PlayUrl = stream.PlayUrl;
            if (_resumeEpisodeId != page.EpisodeId)
            {
                _resumeEpisodeId = null;
                _resumeAtSeconds = 0;
            }
            _page = page;
            VideoInput = input.Trim();
            DurationSeconds = Math.Max(1, page.PlayUrl.Dash.Duration);
            SeekPositionSeconds = 0;
            FilmTitle = $"{detail.VideoInfoView?.Title} · {page.Name}";
            PopulateTracks(page);
            Status = SelectedQuality == null
                ? "此片当前没有 4K 轨道；请选择可用清晰度后播放。"
                : "解析完成。已选 4K，可直接在线播放。";
            if (SelectedAudio?.Id == 30232 && AudioOptions.Any(option => option.Id == 30280))
            {
                Status += " 在线播放默认中质量音轨以改善跳转；可手动选择高质量。";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception error) when (IsRoutineError(error))
        {
            Status = "解析失败。请确认账号仍有效、影片可访问及网络连接。";
        }
    }

    private void PopulateTracks(VideoPage page)
    {
        QualityOptions.Clear();
        AudioOptions.Clear();
        var playUrl = page.PlayUrl!;
        foreach (var track in playUrl.Dash.Video
                     .OrderByDescending(track => track.Id)
                     .ThenByDescending(track => track.CodecId))
        {
            var label = PlaybackQualityCatalog.GetResolutions()
                .FirstOrDefault(quality => quality.Id == track.Id)?.Name ?? $"质量 {track.Id}";
            QualityOptions.Add(new WatchQualityOption(track.Id, track.CodecId,
                $"{label} · {track.Width}×{track.Height} · {track.Codecs}"));
        }

        var audioTracks = playUrl.Dash.Audio
            .Concat(playUrl.Dash.Dolby?.Audio ?? [])
            .Concat(playUrl.Dash.Flac?.Audio is { } flac ? [flac] : []);
        foreach (var track in audioTracks.OrderByDescending(track => track.Id))
        {
            var label = PlaybackQualityCatalog.GetAudioQualities()
                .FirstOrDefault(audio => audio.Id == track.Id)?.Name ?? $"音轨 {track.Id}";
            AudioOptions.Add(new WatchAudioOption(track.Id, $"{label} · {track.Codecs}"));
        }

        SelectedQuality = QualityOptions.FirstOrDefault(option => option.Quality == 120);
        SelectedAudio = AudioOptions.FirstOrDefault(option => option.Id == 30232)
                        ?? AudioOptions.FirstOrDefault();
    }

    private async Task StartPlaybackAsync(bool startPaused, CancellationToken cancellationToken)
    {
        if (_page == null || SelectedQuality == null || SelectedAudio == null)
        {
            Status = "请选择可播放的视频和音轨。";
            return;
        }

        try
        {
            var tracks = PlaybackTrackSelector.Select(_page, SelectedQuality.Quality,
                SelectedQuality.CodecId, SelectedAudio.Id);
            StopPlayback();
            Status = "正在缓冲音视频…";
            _player = await MpvPlaybackSession.StartAsync(tracks,
                _settings.Current.Network.UserAgent, cancellationToken, startPaused).ConfigureAwait(true);
            if (_resumeEpisodeId == _page.EpisodeId && _resumeAtSeconds > 0)
            {
                await _player.SeekAsync(_resumeAtSeconds, cancellationToken).ConfigureAwait(true);
                _resumeEpisodeId = null;
                _resumeAtSeconds = 0;
            }
            MediaInfo = $"已选轨道：{tracks.Width} × {tracks.Height} · {tracks.VideoCodec} · 音频 {tracks.AudioCodec}；等待解码确认";
            Status = startPaused ? "已缓冲，等待房间同步。" : "在线播放中。";
            _recoveries = 0;
            if (_room.Connected)
            {
                await _room.SendAsync(new { type = "ready", ready = true }, cancellationToken)
                    .ConfigureAwait(true);
            }
        }
        catch (Exception error) when (IsRoutineError(error))
        {
            Status = error is InvalidOperationException ? error.Message : "播放失败，请检查网络与 mpv。";
        }
    }

    private Task PlayAsync()
    {
        if (_room.Connected && _player == null)
        {
            return StartPlaybackAsync(true, _lifetime.Token);
        }

        return _room.Connected
            ? SendHostControlAsync(new { type = "play" })
            : StartOrResumeLocalAsync();
    }

    private async Task StartOrResumeLocalAsync()
    {
        if (_player == null)
        {
            await StartPlaybackAsync(false, _lifetime.Token).ConfigureAwait(true);
        }
        else
        {
            await _player.SetPausedAsync(false, _lifetime.Token).ConfigureAwait(true);
        }
    }

    private Task PauseAsync()
    {
        return _room.Connected
            ? SendHostControlAsync(new { type = "pause" })
            : _player?.SetPausedAsync(true, _lifetime.Token) ?? Task.CompletedTask;
    }

    private async Task SeekAsync()
    {
        if (!double.TryParse(SeekSeconds, NumberStyles.Float, CultureInfo.InvariantCulture,
                out var seconds) || !double.IsFinite(seconds) || seconds < 0)
        {
            Status = "请输入有效的跳转秒数。";
            return;
        }

        if (_room.Connected)
        {
            await SendHostControlAsync(new { type = "seek", positionSeconds = seconds })
                .ConfigureAwait(true);
        }
        else if (_player != null)
        {
            await _player.SeekAsync(seconds, _lifetime.Token).ConfigureAwait(true);
        }
    }

    public void BeginSeekDrag() => _seekDragging = true;

    public async Task EndSeekDragAsync()
    {
        _seekDragging = false;
        SeekSeconds = SeekPositionSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        await RunControlSafelyAsync(SeekAsync).ConfigureAwait(true);
    }

    private async Task SetRateAsync()
    {
        if (!double.TryParse(RateText, NumberStyles.Float, CultureInfo.InvariantCulture,
                out var rate) || !double.IsFinite(rate) || rate is < 0.5 or > 2)
        {
            Status = "倍速范围为 0.5–2.0。";
            return;
        }

        if (_room.Connected)
        {
            await SendHostControlAsync(new { type = "rate", rate }).ConfigureAwait(true);
        }
        else if (_player != null)
        {
            await _player.SetSpeedAsync(rate, _lifetime.Token).ConfigureAwait(true);
        }
    }

    private async Task SendHostControlAsync(object message)
    {
        if (!_room.IsHost)
        {
            Status = "房间内由房主控制播放。";
            return;
        }

        try
        {
            await _room.SendAsync(message, _lifetime.Token).ConfigureAwait(true);
        }
        catch (Exception error) when (IsRoutineError(error))
        {
            RoomState = "房间连接中断，正在重连。";
        }
    }

    private async Task RunControlSafelyAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception error) when (IsRoutineError(error))
        {
            Status = error is InvalidOperationException
                ? error.Message
                : "播放控制失败，请检查播放器或房间连接。";
        }
    }

    private async Task StopAsync()
    {
        if (_room.Connected)
        {
            try
            {
                await _room.SendAsync(new { type = "ready", ready = false }, _lifetime.Token)
                    .ConfigureAwait(true);
            }
            catch (Exception error) when (IsRoutineError(error))
            {
                RoomState = "房间连接已断开。";
            }
        }

        StopPlayback();
        Status = "播放已停止，临时缓存已释放。";
    }

    private async Task CreateRoomAsync()
    {
        if (_page?.EpisodeId is not > 0)
        {
            Status = "请先解析一部影片。";
            return;
        }

        try
        {
            if (_room.Connected)
            {
                await LeaveRoomAsync().ConfigureAwait(true);
            }

            var snapshot = await _room.ConnectAsync(ServiceAddress, create: true, null,
                _lifetime.Token).ConfigureAwait(true);
            _lastVersion = -1;
            _startWhenReady = true;
            InviteText = $"{ServiceAddress}#room={_room.RoomCode}";
            RoomState = "房间已创建，等待对方加入。";
            OnPropertyChanged(nameof(CanControl));
            await _room.SendAsync(new
            {
                type = "select",
                media = new { episodeId = _page.EpisodeId }
            }, _lifetime.Token).ConfigureAwait(true);
            await StartPlaybackAsync(true, _lifetime.Token).ConfigureAwait(true);
            await ApplySnapshotAsync(snapshot).ConfigureAwait(true);
        }
        catch (Exception error) when (IsRoutineError(error))
        {
            RoomState = "创建房间失败，请检查服务地址和网络。";
        }
    }

    private async Task JoinRoomAsync()
    {
        try
        {
            var (address, code) = ParseInvite(InviteText, ServiceAddress);
            if (_room.Connected)
            {
                await LeaveRoomAsync().ConfigureAwait(true);
            }

            ServiceAddress = address;
            RoomState = "正在加入房间…";
            var snapshot = await _room.ConnectAsync(address, create: false, code,
                _lifetime.Token).ConfigureAwait(true);
            _lastVersion = -1;
            OnPropertyChanged(nameof(CanControl));
            await ApplySnapshotAsync(snapshot).ConfigureAwait(true);
        }
        catch (Exception error) when (IsRoutineError(error))
        {
            RoomState = "加入失败，请检查邀请、人数和服务连接。";
        }
    }

    private static (string Address, string Code) ParseInvite(string invite, string defaultAddress)
    {
        if (Uri.TryCreate(invite, UriKind.Absolute, out var uri) && uri.Fragment.StartsWith("#room=", StringComparison.Ordinal))
        {
            return (uri.GetLeftPart(UriPartial.Path), Uri.UnescapeDataString(uri.Fragment[6..]));
        }

        return (defaultAddress, invite.Trim());
    }

    private async Task CopyInviteAsync()
    {
        if (!string.IsNullOrEmpty(InviteText))
        {
            await _clipboard.SetTextAsync(InviteText, _lifetime.Token).ConfigureAwait(true);
            RoomState = "邀请已复制，可发给对方。";
        }
    }

    private async Task LeaveRoomAsync()
    {
        if (_room.Connected)
        {
            try
            {
                await _room.SendAsync(new { type = "leave" }, _lifetime.Token).ConfigureAwait(true);
            }
            catch (Exception error) when (IsRoutineError(error))
            {
                // The local room is still released.
            }
        }

        await _room.LeaveAsync().ConfigureAwait(true);
        _lastSnapshot = null;
        _lastVersion = -1;
        _startWhenReady = false;
        SyncStatus = string.Empty;
        RoomState = "未加入房间";
        OnPropertyChanged(nameof(CanControl));
        StopPlayback();
    }

    private async Task EndRoomAsync()
    {
        if (_room.Connected && _room.IsHost)
        {
            try
            {
                await _room.SendAsync(new { type = "close" }, _lifetime.Token).ConfigureAwait(true);
            }
            catch (Exception error) when (IsRoutineError(error))
            {
                RoomState = "房间连接已断开。";
            }
        }

        await LeaveRoomAsync().ConfigureAwait(true);
    }

    private void OnSnapshotReceived(WatchRoomSnapshot snapshot)
    {
        Dispatcher.UIThread.Post(() => _ = ApplySnapshotSafelyAsync(snapshot));
    }

    private async Task ApplySnapshotSafelyAsync(WatchRoomSnapshot snapshot)
    {
        try
        {
            await ApplySnapshotAsync(snapshot).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception error) when (IsRoutineError(error))
        {
            Status = "房间同步失败，请检查本机媒体与网络。";
        }
    }

    private async Task ApplySnapshotAsync(WatchRoomSnapshot snapshot)
    {
        await _snapshotGate.WaitAsync(_lifetime.Token).ConfigureAwait(true);
        try
        {
            if (snapshot.Version <= _lastVersion)
            {
                return;
            }

            _lastVersion = snapshot.Version;
            _lastSnapshot = snapshot;
            _lastSnapshotReceivedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            RoomState = !(_room.Connected)
                ? "重连中"
                : snapshot.Host?.Online != true
                    ? "房主离线，等待重连"
                    : snapshot.Guest?.Online != true
                        ? "等待对方加入"
                        : snapshot.WaitingForReady
                            ? "等待双方缓冲就绪"
                            : snapshot.Guest?.Buffering == true || snapshot.Host.Buffering
                                ? "一方缓冲中，等待恢复"
                                : "双方在线，已同步";
            if (snapshot.Media is not { EpisodeId: > 0 } media)
            {
                return;
            }

            if (_room.IsHost && _startWhenReady && snapshot.Host?.Ready == true
                && snapshot.Guest is { Online: true, Ready: true, Buffering: false })
            {
                _startWhenReady = false;
                await _room.SendAsync(new { type = "play" }, _lifetime.Token).ConfigureAwait(true);
            }

            if (_page?.EpisodeId != media.EpisodeId)
            {
                await ParseInputAsync($"https://www.bilibili.com/bangumi/play/ep{media.EpisodeId}",
                    _lifetime.Token).ConfigureAwait(true);
                if (_page?.EpisodeId != media.EpisodeId || SelectedQuality == null)
                {
                    RoomState = "本机未取得此片 4K；请选择轨道后点击播放。";
                    return;
                }

                await StartPlaybackAsync(true, _lifetime.Token).ConfigureAwait(true);
            }

            await CorrectPlaybackAsync(snapshot, _lifetime.Token).ConfigureAwait(true);
        }
        finally
        {
            _snapshotGate.Release();
        }
    }

    private async Task CorrectPlaybackAsync(WatchRoomSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (_player == null || snapshot.Media?.EpisodeId != _page?.EpisodeId)
        {
            return;
        }

        var expected = snapshot.PositionSeconds;
        if (snapshot.Playing && !snapshot.WaitingForReady)
        {
            var localNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var elapsedMilliseconds = _room.HasClockEstimate
                ? localNow + _room.ClockOffsetMilliseconds - snapshot.ServerTimeUnixMs
                : localNow - _lastSnapshotReceivedAtUnixMs;
            expected += Math.Max(0, elapsedMilliseconds) / 1000.0 * snapshot.Rate;
        }

        var actual = await _player.GetPositionAsync(cancellationToken).ConfigureAwait(true);
        SyncStatus = $"影片时间差 {Math.Abs(expected - actual):0.00} 秒 · 往返延迟 {_room.RoundTripMilliseconds:0} 毫秒";
        if (Math.Abs(expected - actual) > 0.45)
        {
            await _player.SeekAsync(expected, cancellationToken).ConfigureAwait(true);
        }

        await _player.SetSpeedAsync(snapshot.Rate, cancellationToken).ConfigureAwait(true);
        await _player.SetPausedAsync(!snapshot.Playing || snapshot.WaitingForReady
            || snapshot.Host?.Online != true, cancellationToken).ConfigureAwait(true);
    }

    private void OnRoomDisconnected()
    {
        Dispatcher.UIThread.Post(() => _ = ReconnectSafelyAsync());
    }

    private void OnRoomClosed()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _lastSnapshot = null;
            _startWhenReady = false;
            RoomState = "房间已结束。";
            OnPropertyChanged(nameof(CanControl));
            StopPlayback();
        });
    }

    private async Task ReconnectSafelyAsync()
    {
        var code = _room.RoomCode;
        if (code == null || _lifetime.IsCancellationRequested)
        {
            return;
        }

        try
        {
            RoomState = "房间断线，正在重连…";
            if (_player != null)
            {
                await _player.SetPausedAsync(true, _lifetime.Token).ConfigureAwait(true);
            }

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt), _lifetime.Token).ConfigureAwait(true);
                    var snapshot = await _room.ConnectAsync(ServiceAddress, create: false, code,
                        _lifetime.Token).ConfigureAwait(true);
                    _lastVersion = -1;
                    if (_player != null)
                    {
                        await _room.SendAsync(new { type = "ready", ready = true }, _lifetime.Token)
                            .ConfigureAwait(true);
                    }

                    await ApplySnapshotAsync(snapshot).ConfigureAwait(true);
                    return;
                }
                catch (Exception error) when (IsRoutineError(error))
                {
                    continue;
                }
            }

            RoomState = "重连失败；可重新粘贴邀请加入。";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception error) when (IsRoutineError(error))
        {
            RoomState = "重连失败；请检查网络后重新加入。";
        }
    }

    private async Task MonitorPlaybackAsync(CancellationToken cancellationToken)
    {
        try
        {
            var ticks = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(1000, cancellationToken).ConfigureAwait(true);
                var player = _player;
                if (player == null)
                {
                    continue;
                }

                if (player.HasExited)
                {
                    if (_page?.PlayUrl?.Dash.Duration is > 0
                        && _lastPosition >= _page.PlayUrl.Dash.Duration - 3)
                    {
                        Status = "播放完成。";
                        StopPlayback();
                    }
                    else
                    {
                        await RecoverPlaybackAsync(cancellationToken).ConfigureAwait(true);
                    }

                    continue;
                }

                try
                {
                    _lastPosition = await player.GetPositionAsync(cancellationToken).ConfigureAwait(true);
                    PositionText = TimeSpan.FromSeconds(_lastPosition).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
                    if (!_seekDragging)
                    {
                        SeekPositionSeconds = Math.Clamp(_lastPosition, 0, DurationSeconds);
                    }
                    if (SelectedQuality != null && SelectedAudio != null)
                    {
                        var decoded = await player.GetDecodedDimensionsAsync(cancellationToken).ConfigureAwait(true);
                        if (decoded is { } size)
                        {
                            MediaInfo = $"播放器解码：{size.Width} × {size.Height} · 已选 {SelectedQuality.Display} · 音频 {SelectedAudio.Display}";
                        }
                    }
                    var buffering = await player.GetBufferingAsync(cancellationToken).ConfigureAwait(true);
                    if (buffering != _lastBuffering && _room.Connected)
                    {
                        _lastBuffering = buffering;
                        await _room.SendAsync(new { type = "buffering", buffering }, cancellationToken)
                            .ConfigureAwait(true);
                    }

                    if (_lastSnapshot != null && _room.Connected)
                    {
                        await CorrectPlaybackAsync(_lastSnapshot, cancellationToken).ConfigureAwait(true);
                    }

                    if (++ticks % 5 == 0 && _room.Connected)
                    {
                        await _room.PingAsync(cancellationToken).ConfigureAwait(true);
                    }
                }
                catch (Exception error) when (IsRoutineError(error))
                {
                    Status = "媒体连接不稳定，正在检查播放状态。";
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception error) when (IsRoutineError(error))
        {
            Status = "播放器状态监控已停止，请重新打开观影模式。";
        }
    }

    private async Task RecoverPlaybackAsync(CancellationToken cancellationToken)
    {
        if (_page == null || _recoveries >= 2)
        {
            Status = "播放已结束，或刷新地址失败。可重新解析再播放。";
            StopPlayback();
            return;
        }

        _recoveries++;
        Status = "媒体连接中断，正在重新解析并恢复进度…";
        try
        {
            var result = await _parser.LoadPageStreamAsync(VideoInput, _page,
                refresh: true, cancellationToken).ConfigureAwait(true);
            if (result?.PlayUrl == null)
            {
                throw new InvalidOperationException("媒体地址刷新失败。");
            }

            _page.PlayUrl = result.PlayUrl;
            var quality = SelectedQuality ?? throw new InvalidOperationException("视频轨道未选定。");
            var audio = SelectedAudio ?? throw new InvalidOperationException("音轨未选定。");
            var tracks = PlaybackTrackSelector.Select(_page, quality.Quality, quality.CodecId, audio.Id);
            StopPlayback();
            _player = await MpvPlaybackSession.StartAsync(tracks,
                _settings.Current.Network.UserAgent, cancellationToken, startPaused: true).ConfigureAwait(true);
            await _player.SeekAsync(_lastPosition, cancellationToken).ConfigureAwait(true);
            if (_lastSnapshot != null && _room.Connected)
            {
                await CorrectPlaybackAsync(_lastSnapshot, cancellationToken).ConfigureAwait(true);
            }
            else
            {
                await _player.SetPausedAsync(false, cancellationToken).ConfigureAwait(true);
            }

            Status = "已恢复播放。";
        }
        catch (Exception error) when (IsRoutineError(error))
        {
            _resumeEpisodeId = _page?.EpisodeId;
            _resumeAtSeconds = _lastPosition;
            Status = "地址刷新失败；若登录已过期，请重新扫码后继续。";
        }
    }

    private void StopPlayback()
    {
        _player?.Dispose();
        _player = null;
        MediaInfo = string.Empty;
        _lastBuffering = false;
    }

    private static bool IsRoutineError(Exception error)
    {
        return error is InvalidOperationException or ArgumentException or FormatException
            or System.IO.IOException or System.Net.Http.HttpRequestException
            or WebSocketException or System.ComponentModel.Win32Exception
            or Newtonsoft.Json.JsonException or System.Text.Json.JsonException;
    }

    public void StopForWindowClose()
    {
        _lifetime.Cancel();
        StopPlayback();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _monitorTask.ConfigureAwait(false);
        if (_loginCancellation != null)
        {
            await _loginCancellation.CancelAsync().ConfigureAwait(false);
        }
        _loginCancellation?.Dispose();
        _room.SnapshotReceived -= OnSnapshotReceived;
        _room.Disconnected -= OnRoomDisconnected;
        _room.Closed -= OnRoomClosed;
        await _room.DisposeAsync().ConfigureAwait(false);
        StopPlayback();
        LoginQrCode?.Dispose();
        LoginQrCode = null;
        _snapshotGate.Dispose();
        _lifetime.Dispose();
    }
}

internal sealed record WatchQualityOption(int Quality, int CodecId, string Display);
internal sealed record WatchAudioOption(int Id, string Display);
