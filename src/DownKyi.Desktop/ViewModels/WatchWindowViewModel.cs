using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.Login;
using DownKyi.Presentation;
using DownKyi.RoomServer;
using DownKyi.Services.Account;
using DownKyi.Services.Video;
using DownKyi.Services.Watch;
using Microsoft.AspNetCore.Builder;

namespace DownKyi.ViewModels;

internal sealed class WatchWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ILoginCoordinator _login;
    private readonly ILoginQrCodeRenderer _qrRenderer;
    private readonly IUserSessionCoordinator _session;
    private readonly IClipboardService _clipboard;
    private readonly VideoParseCoordinator _parser;
    private readonly MainWindowViewModel _downloadContent;
    private readonly WatchRoomClient _room = new();
    private readonly QuickRoomTunnel _quickTunnel = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _snapshotGate = new(1, 1);
    private readonly Task _monitorTask;
    private CancellationTokenSource? _loginCancellation;
    private BilibiliWebPlaybackSession? _roomPlayer;
    private NativeWebView? _roomBrowser;
    private BilibiliWebPlaybackSession? _player => _roomPlayer;
    private VideoPage? _roomPage;
    private WatchRoomSnapshot? _lastSnapshot;
    private long _lastSnapshotReceivedAtUnixMs;
    private long _lastVersion = -1;
    private double _lastPosition;
    private long? _resumeEpisodeId;
    private double _resumeAtSeconds;
    private bool _seekDragging;
    private bool _lastBuffering;
    private bool _startWhenReady;
    private bool _playerLoading;
    private int _recoveries;
    private bool _disposed;
    private bool _isPreparing;
    public bool IsPreparing { get => _isPreparing; private set => SetProperty(ref _isPreparing, value); }
    private bool _downloadLoaded;
    private string _connectAddress = "ws://127.0.0.1:5077/ws";
    private string? _roomParsedInput;
    private string? _hostInviteUrl;
    private WebApplication? _localServer;

    public MainWindowViewModel DownloadContent => _downloadContent;
    private string _selectedSection = "Room";
    public bool ShowLogin => _selectedSection == "Login";
    public bool ShowRoom => _selectedSection == "Room";
    public bool ShowDownload => _selectedSection == "Download";
    public bool ShowPlayer => ShowRoom;

    public IRelayCommand ShowLoginCommand { get; }
    public IRelayCommand ShowRoomCommand { get; }
    public IRelayCommand ShowDownloadCommand { get; }

    private string _status = "未登录。请扫码登录。";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    private double _playerOpacity;
    public double PlayerOpacity { get => _playerOpacity; private set => SetProperty(ref _playerOpacity, value); }

    private bool _playerReady;
    public bool PlayerReady { get => _playerReady; private set => SetProperty(ref _playerReady, value); }

    private string _loginStatus = "未登录。请扫码登录。";
    public string LoginStatus { get => _loginStatus; private set => SetProperty(ref _loginStatus, value); }

    private bool _isLoggedIn;
    public bool IsLoggedIn { get => _isLoggedIn; private set => SetProperty(ref _isLoggedIn, value); }

    private string _accountStatus = "正在检查登录状态…";
    public string AccountStatus { get => _accountStatus; private set => SetProperty(ref _accountStatus, value); }

    private Bitmap? _loginQrCode;
    private Uri? _loginQrUri;
    public Bitmap? LoginQrCode
    {
        get => _loginQrCode;
        private set
        {
            if (SetProperty(ref _loginQrCode, value))
            {
                OnPropertyChanged(nameof(HasLoginQrCode));
            }
        }
    }
    public bool HasLoginQrCode => LoginQrCode != null;

    private string _roomVideoInput = string.Empty;
    public string RoomVideoInput { get => _roomVideoInput; set => SetProperty(ref _roomVideoInput, value); }

    private string _roomFilmTitle = string.Empty;
    public string RoomFilmTitle { get => _roomFilmTitle; private set => SetProperty(ref _roomFilmTitle, value); }

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

    private int _roomMemberCount;
    public string RoomParticipantText => _roomMemberCount > 0
        ? $"当前房间人数：{_roomMemberCount} 人"
        : "尚未创建房间 · 可单人播放";
    public bool IsInRoom => _room.Connected;
    public string RoomRoleText => _room.IsHost ? "房主" : "访客";

    private string _syncStatus = string.Empty;
    public string SyncStatus { get => _syncStatus; private set => SetProperty(ref _syncStatus, value); }

    public bool CanControl => !_room.Connected || _room.IsHost;

    public IAsyncRelayCommand LoginCommand { get; }
    public IAsyncRelayCommand CopyLoginQrCommand { get; }
    public IAsyncRelayCommand LogoutCommand { get; }
    public IAsyncRelayCommand ParseCommand { get; }
    public IAsyncRelayCommand SeekCommand { get; }
    public IAsyncRelayCommand RateCommand { get; }
    public IAsyncRelayCommand CreateRoomCommand { get; }
    public IAsyncRelayCommand JoinRoomCommand { get; }
    public IAsyncRelayCommand CopyInviteCommand { get; }
    public IAsyncRelayCommand LeaveRoomCommand { get; }
    public IAsyncRelayCommand EndRoomCommand { get; }

    public WatchWindowViewModel(
        ILoginCoordinator login,
        ILoginQrCodeRenderer qrRenderer,
        IUserSessionCoordinator session,
        IClipboardService clipboard,
        VideoParseCoordinator parser,
        MainWindowViewModel downloadContent)
    {
        _login = login;
        _qrRenderer = qrRenderer;
        _session = session;
        _clipboard = clipboard;
        _parser = parser;
        _downloadContent = downloadContent;
        _room.SnapshotReceived += OnSnapshotReceived;
        _room.Disconnected += OnRoomDisconnected;
        _room.Closed += OnRoomClosed;
        ShowLoginCommand = new RelayCommand(() => SelectSection("Login"));
        ShowRoomCommand = new RelayCommand(() => SelectSection("Room"));
        ShowDownloadCommand = new RelayCommand(() => _ = OpenDownloadAsync());
        LoginCommand = new AsyncRelayCommand(StartLoginAsync);
        CopyLoginQrCommand = new AsyncRelayCommand(CopyLoginQrAsync);
        LogoutCommand = new AsyncRelayCommand(LogoutAsync);
        ParseCommand = new AsyncRelayCommand(ParseAsync);
        SeekCommand = new AsyncRelayCommand(() => RunControlSafelyAsync(SeekAsync));
        RateCommand = new AsyncRelayCommand(() => RunControlSafelyAsync(SetRateAsync));
        CreateRoomCommand = new AsyncRelayCommand(CreateRoomAsync);
        JoinRoomCommand = new AsyncRelayCommand(JoinRoomAsync);
        CopyInviteCommand = new AsyncRelayCommand(CopyInviteAsync);
        LeaveRoomCommand = new AsyncRelayCommand(LeaveRoomAsync);
        EndRoomCommand = new AsyncRelayCommand(EndRoomAsync);
        _monitorTask = MonitorPlaybackAsync(_lifetime.Token);
    }

    public async Task InitializeAsync()
    {
        IsPreparing = true;
        try
        {
            var snapshot = await _session.RefreshAsync(_lifetime.Token).ConfigureAwait(true);
            IsLoggedIn = snapshot.UserInfo?.IsLogin == true;
            AccountStatus = IsLoggedIn
                ? $"已登录：{snapshot.UserInfo!.Name} · UID {snapshot.UserInfo.Mid}"
                  + (snapshot.UserInfo.VipStatus == 1 ? " · 大会员" : string.Empty)
                : "未登录 B 站账号";
            LoginStatus = IsLoggedIn ? "已登录，可以开始观影或下载。" : "未登录。请扫码登录。";
            if (_player == null)
            {
                Status = IsLoggedIn ? "已登录。粘贴影片链接并解析后即可播放。" : "未登录。请扫码登录。";
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception error) when (IsRoutineError(error))
        {
            AccountStatus = "登录状态检查失败";
            LoginStatus = "登录状态检查失败，请检查网络后重试扫码。";
            if (_player == null)
            {
                Status = "登录状态检查失败，请检查网络后重试。";
            }
        }
        finally
        {
            IsPreparing = false;
        }
    }

    public void AttachBrowser(NativeWebView roomBrowser)
    {
        _roomBrowser = roomBrowser;
        roomBrowser.WebMessageReceived += OnWebMessageReceived;
    }

    private void SelectSection(string section)
    {
        _selectedSection = section;
        OnPropertyChanged(nameof(ShowLogin));
        OnPropertyChanged(nameof(ShowRoom));
        OnPropertyChanged(nameof(ShowDownload));
        OnPropertyChanged(nameof(ShowPlayer));
    }

    private async Task OpenDownloadAsync()
    {
        SelectSection("Download");
        if (_downloadLoaded)
        {
            return;
        }

        _downloadLoaded = true;
        IsPreparing = true;
        try
        {
            await Task.Run(BundledTools.EnsureDownloadTools, _lifetime.Token).ConfigureAwait(true);
            _downloadContent.LoadedCommand?.Execute(null);
        }
        catch (Exception error) when (IsRoutineError(error))
        {
            Status = "下载工具准备失败，请检查本机存储空间或重新构建程序。";
            _downloadLoaded = false;
        }
        finally
        {
            IsPreparing = false;
        }
    }

    private void OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs args)
    {
        if (!_room.Connected || !_room.IsHost || _roomPlayer == null || _disposed)
        {
            return;
        }

        try
        {
            using var message = JsonDocument.Parse(args.Body ?? string.Empty);
            var root = message.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("source", out var source)
                || source.ValueKind != JsonValueKind.String
                || source.GetString() != "biliCinemaPlayer"
                || !root.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String)
            {
                return;
            }

            object? command = type.GetString() switch
            {
                "play" => new { type = "play" },
                "pause" => new { type = "pause" },
                "seeked" when root.TryGetProperty("position", out var position)
                              && position.ValueKind == JsonValueKind.Number
                              && position.TryGetDouble(out var seconds)
                              && double.IsFinite(seconds) && seconds is >= 0 and <= 86400
                    => new { type = "seek", positionSeconds = seconds },
                "ratechange" when root.TryGetProperty("rate", out var rate)
                                  && rate.ValueKind == JsonValueKind.Number
                                  && rate.TryGetDouble(out var speed)
                                  && double.IsFinite(speed) && speed is >= 0.25 and <= 3
                    => new { type = "rate", rate = speed },
                _ => null
            };
            if (command != null)
            {
                _ = RunControlSafelyAsync(() => SendHostControlAsync(command));
            }
        }
        catch (JsonException)
        {
            // Ignore unrelated messages from the Bilibili page.
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
            LoginQrCode?.Dispose();
            LoginQrCode = null;
            _loginQrUri = null;
            LoginStatus = "正在生成登录二维码…";
            var loginUrl = await _login.RequestLoginUrlAsync(cancellationToken).ConfigureAwait(true);
            if (loginUrl?.Code != 0 || loginUrl.Data?.QrCodeAddress == null
                || loginUrl.Data.QrcodeKey == null)
            {
                LoginStatus = "二维码生成失败，请重试。";
                return;
            }

            var uri = new Uri(loginUrl.Data.QrCodeAddress, UriKind.Absolute);
            LoginQrCode?.Dispose();
            LoginQrCode = _qrRenderer.Render(uri);
            _loginQrUri = uri;
            LoginStatus = "请用哔哩哔哩 App 扫码并确认；也可复制二维码图片私下发送。";
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
                    LoginStatus = "二维码已过期，请重新获取。";
                    LoginQrCode?.Dispose();
                    LoginQrCode = null;
                    _loginQrUri = null;
                    return;
                }

                if (data.Code == 0)
                {
                    var redirect = new Uri(data.RedirectAddress, UriKind.Absolute);
                    if (!await _login.SaveLoginCookiesAsync(result!, redirect, cancellationToken)
                            .ConfigureAwait(true))
                    {
                        LoginStatus = "登录验证失败，请重新扫码。";
                        return;
                    }

                    await InitializeAsync().ConfigureAwait(true);
                    LoginQrCode?.Dispose();
                    LoginQrCode = null;
                    _loginQrUri = null;
                    if (_lastSnapshot != null && _room.Connected)
                    {
                        _lastVersion = -1;
                        await ApplySnapshotAsync(_lastSnapshot).ConfigureAwait(true);
                    }
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
            LoginStatus = "扫码登录失败，请检查网络并重试。";
        }
    }

    private async Task CopyLoginQrAsync()
    {
        if (LoginQrCode == null || _loginQrUri == null)
        {
            LoginStatus = "请先获取有效的登录二维码。";
            return;
        }

        try
        {
            var image = LoginQrShareImageRenderer.Render(_loginQrUri);
            await _clipboard.SetPngImageAsync(image, _lifetime.Token).ConfigureAwait(true);
            LoginStatus = "登录分享卡片已复制到剪贴板。";
        }
        catch (Exception error) when (IsRoutineError(error))
        {
            LoginStatus = "复制二维码失败，请重试。";
        }
    }

    private async Task LogoutAsync()
    {
        await LeaveRoomAsync().ConfigureAwait(true);
        StopPlayback();
        var browserCookiesCleared = true;
        try
        {
            await BilibiliWebPlaybackSession.ClearBilibiliCookiesAsync(_roomBrowser).ConfigureAwait(true);
        }
        catch (Exception error) when (IsRoutineError(error))
        {
            browserCookiesCleared = false;
        }
        if (_loginCancellation != null)
        {
            await _loginCancellation.CancelAsync().ConfigureAwait(true);
        }
        IsLoggedIn = false;
        AccountStatus = "未登录 B 站账号";
        if (!LoginHelper.DeleteLoginInfoCookies())
        {
            LoginStatus = "本机登录信息清理失败，请检查数据目录权限。";
            return;
        }

        await InitializeAsync().ConfigureAwait(true);
        LoginStatus = browserCookiesCleared
            ? "已退出本机 B 站账号。"
            : "已删除本机登录信息；请关闭观影窗口以结束网页会话。";
    }

    private async Task ParseAsync()
    {
        if (_room.Connected && !_room.IsHost)
        {
            Status = "房间内由房主选择影片。";
            return;
        }

        var input = RoomVideoInput.Trim();
        await ParseInputAsync(input, _lifetime.Token)
            .ConfigureAwait(true);
        if (!IsLoggedIn || _roomPage?.EpisodeId is not > 0 || _roomParsedInput != input)
        {
            return;
        }

        if (_room.IsHost)
        {
            await SendHostControlAsync(new
            {
                type = "select",
                media = new { episodeId = _roomPage.EpisodeId }
            }).ConfigureAwait(true);
        }

        await StartPlaybackAsync(startPaused: true, _lifetime.Token).ConfigureAwait(true);
    }

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
            _roomPage = null;
            _roomParsedInput = null;
            RoomFilmTitle = string.Empty;
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
            if (page.EpisodeId <= 0)
            {
                Status = "仅观影模式目前支持 B 站番剧和电影剧集链接。";
                return;
            }

            if (_resumeEpisodeId != page.EpisodeId)
            {
                _resumeEpisodeId = null;
                _resumeAtSeconds = 0;
            }
            _roomPage = page;
            _roomParsedInput = input.Trim();
            RoomFilmTitle = $"{detail.VideoInfoView?.Title} · {page.Name}";
            if (_room.Connected && !_room.IsHost)
            {
                RoomVideoInput = input.Trim();
            }
            DurationSeconds = 1;
            SeekPositionSeconds = 0;
            Status = "影片已解析，正在准备画面…";
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

    private async Task StartPlaybackAsync(bool startPaused, CancellationToken cancellationToken)
    {
        if (_playerLoading)
        {
            return;
        }

        var page = _roomPage;
        var browser = _roomBrowser;
        if (page?.EpisodeId is not > 0 || browser == null)
        {
            Status = "请先解析影片，并等待网页播放器准备好。";
            return;
        }

        try
        {
            _playerLoading = true;
            StopPlayback();
            Status = "正在打开 B 站网页播放器…";
            var player = await BilibiliWebPlaybackSession.StartAsync(browser, page,
                startPaused, cancellationToken).ConfigureAwait(true);
            _roomPlayer = player;
            if (_resumeEpisodeId == page.EpisodeId && _resumeAtSeconds > 0)
            {
                await player.SeekAsync(_resumeAtSeconds, cancellationToken).ConfigureAwait(true);
                _resumeEpisodeId = null;
                _resumeAtSeconds = 0;
            }
            PlayerReady = true;
            PlayerOpacity = 1;
            Status = startPaused ? "画面已就绪，可在视频内播放和调整画质。" : "B 站网页在线播放中。";
            _recoveries = 0;
            if (_room.Connected)
            {
                await _room.SendAsync(new { type = "ready", ready = true }, cancellationToken)
                    .ConfigureAwait(true);
            }
        }
        catch (Exception error) when (IsRoutineError(error))
        {
            Status = error is InvalidOperationException ? error.Message : "网页播放失败，请检查网络和登录状态。";
        }
        finally
        {
            _playerLoading = false;
        }
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

    private async Task CreateRoomAsync()
    {
        if (_room.Connected)
        {
            RoomState = "已经在房间内；请先结束或离开当前房间。";
            return;
        }

        if (string.IsNullOrWhiteSpace(RoomVideoInput))
        {
            Status = "请在观影房间页面填写影片链接。";
            return;
        }

        if (!string.Equals(_roomParsedInput, RoomVideoInput.Trim(), StringComparison.Ordinal))
        {
            await ParseInputAsync(RoomVideoInput, _lifetime.Token).ConfigureAwait(true);
        }
        if (_roomPage?.EpisodeId is not > 0)
        {
            Status = "请先在观影房间页面解析影片链接。";
            return;
        }

        try
        {
            var resumePosition = _player == null
                ? 0
                : await _player.GetPositionAsync(_lifetime.Token).ConfigureAwait(true);
            var resumePlaying = _player == null
                || !await _player.GetPausedAsync(_lifetime.Token).ConfigureAwait(true);
            if (ServiceAddress == "ws://127.0.0.1:5077/ws"
                || ServiceAddress == _quickTunnel.ServiceAddress)
            {
                IsPreparing = true;
                await EnsureLocalRoomServerAsync(_lifetime.Token).ConfigureAwait(true);
                RoomState = "正在生成 Cloudflare 房间地址…";
                ServiceAddress = await _quickTunnel.StartAsync(_lifetime.Token).ConfigureAwait(true);
                _connectAddress = "ws://127.0.0.1:5077/ws";
            }
            else
            {
                _connectAddress = ServiceAddress;
            }

            var snapshot = await _room.ConnectAsync(_connectAddress, create: true, null,
                _lifetime.Token).ConfigureAwait(true);
            _lastVersion = -1;
            UpdateRoomMemberCount(snapshot);
            _startWhenReady = resumePlaying;
            _hostInviteUrl = $"{ServiceAddress}#room={_room.RoomCode}";
            InviteText = string.Empty;
            RoomState = "房间已创建，可以单人播放或邀请对方加入。";
            OnPropertyChanged(nameof(CanControl));
            OnPropertyChanged(nameof(IsInRoom));
            OnPropertyChanged(nameof(RoomRoleText));
            await _room.SendAsync(new
            {
                type = "select",
                media = new { episodeId = _roomPage.EpisodeId }
            }, _lifetime.Token).ConfigureAwait(true);
            if (resumePosition > 0)
            {
                _resumeEpisodeId = _roomPage.EpisodeId;
                _resumeAtSeconds = resumePosition;
                await _room.SendAsync(new { type = "seek", positionSeconds = resumePosition },
                    _lifetime.Token).ConfigureAwait(true);
            }
            await StartPlaybackAsync(true, _lifetime.Token).ConfigureAwait(true);
            await ApplySnapshotAsync(snapshot).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception error) when (IsRoutineError(error))
        {
            RoomState = error is InvalidOperationException
                ? error.Message
                : "创建房间失败，请检查服务地址和网络。";
        }
        finally
        {
            IsPreparing = false;
        }
    }

    private async Task JoinRoomAsync()
    {
        try
        {
            if (_room.Connected)
            {
                RoomState = "已经在房间内；无需再次加入。";
                return;
            }

            var (address, code) = ParseInvite(InviteText, ServiceAddress);
            ServiceAddress = address;
            _connectAddress = address;
            RoomState = "正在加入房间…";
            var snapshot = await _room.ConnectAsync(address, create: false, code,
                _lifetime.Token).ConfigureAwait(true);
            _lastVersion = -1;
            UpdateRoomMemberCount(snapshot);
            OnPropertyChanged(nameof(CanControl));
            OnPropertyChanged(nameof(IsInRoom));
            OnPropertyChanged(nameof(RoomRoleText));
            await ApplySnapshotAsync(snapshot).ConfigureAwait(true);
        }
        catch (Exception error) when (IsRoutineError(error))
        {
            RoomState = "加入失败，请检查邀请、人数和服务连接。";
        }
    }

    private async Task EnsureLocalRoomServerAsync(CancellationToken cancellationToken)
    {
        if (_localServer != null)
        {
            return;
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            using var response = await client.GetAsync(new Uri("http://127.0.0.1:5077/health"), cancellationToken)
                .ConfigureAwait(true);
            if (response.IsSuccessStatusCode)
            {
                return;
            }
        }
        catch (HttpRequestException)
        {
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        _localServer = RoomServerHost.Build();
        await _localServer.StartAsync(cancellationToken).ConfigureAwait(true);
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
        if (_room.IsHost && _hostInviteUrl is { } inviteUrl)
        {
            await _clipboard.SetTextAsync(inviteUrl, _lifetime.Token).ConfigureAwait(true);
            RoomState = "邀请已复制，可发给对方。";
        }
        else
        {
            RoomState = "请先创建房间，再复制邀请。";
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
        _hostInviteUrl = null;
        SyncStatus = string.Empty;
        RoomState = "未加入房间，可继续单人播放。";
        _roomMemberCount = 0;
        OnPropertyChanged(nameof(RoomParticipantText));
        OnPropertyChanged(nameof(CanControl));
        OnPropertyChanged(nameof(IsInRoom));
        OnPropertyChanged(nameof(RoomRoleText));
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
            UpdateRoomMemberCount(snapshot);
            RoomState = !(_room.Connected)
                ? "重连中"
                : snapshot.Host?.Online != true
                    ? "房主离线，等待重连"
                    : snapshot.Guest?.Online != true
                        ? snapshot.Playing ? "房间内仅你一人，正在播放" : "房间内仅你一人"
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
                && (snapshot.Guest?.Online != true
                    || snapshot.Guest is { Ready: true, Buffering: false }))
            {
                _startWhenReady = false;
                await _room.SendAsync(new { type = "play" }, _lifetime.Token).ConfigureAwait(true);
            }

            if (_roomPage?.EpisodeId != media.EpisodeId)
            {
                await ParseInputAsync($"https://www.bilibili.com/bangumi/play/ep{media.EpisodeId}",
                    _lifetime.Token).ConfigureAwait(true);
                if (_roomPage?.EpisodeId != media.EpisodeId)
                {
                    RoomState = "本机未能打开此影片，请检查账号权限。";
                    return;
                }

            }

            if (_player == null)
            {
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
        var player = _roomPlayer;
        if (player == null || snapshot.Media?.EpisodeId != _roomPage?.EpisodeId)
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

        var actual = await player.GetPositionAsync(cancellationToken).ConfigureAwait(true);
        var drift = expected - actual;
        SyncStatus = $"影片时间差 {Math.Abs(drift):0.00} 秒 · 往返延迟 {_room.RoundTripMilliseconds:0} 毫秒";
        if (Math.Abs(drift) > 1.5)
        {
            await player.SeekAsync(expected, cancellationToken).ConfigureAwait(true);
        }

        var shouldPause = !snapshot.Playing || snapshot.WaitingForReady
            || snapshot.Host?.Online != true;
        var targetRate = snapshot.Rate;
        if (!shouldPause && Math.Abs(drift) is > 0.45 and <= 1.5)
        {
            // A small rate adjustment avoids repeatedly jumping a few frames.
            targetRate = Math.Clamp(snapshot.Rate + drift * 0.08,
                snapshot.Rate * 0.96, snapshot.Rate * 1.04);
        }

        if (Math.Abs(await player.GetSpeedAsync(cancellationToken).ConfigureAwait(true) - targetRate) > 0.015)
        {
            await player.SetSpeedAsync(targetRate, cancellationToken).ConfigureAwait(true);
        }

        if (await player.GetPausedAsync(cancellationToken).ConfigureAwait(true) != shouldPause)
        {
            await player.SetPausedAsync(shouldPause, cancellationToken).ConfigureAwait(true);
        }
    }

    private void OnRoomDisconnected()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _roomMemberCount = 0;
            OnPropertyChanged(nameof(RoomParticipantText));
            OnPropertyChanged(nameof(IsInRoom));
            OnPropertyChanged(nameof(CanControl));
            _ = ReconnectSafelyAsync();
        });
    }

    private void OnRoomClosed()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _lastSnapshot = null;
            _startWhenReady = false;
            _hostInviteUrl = null;
            _roomMemberCount = 0;
            OnPropertyChanged(nameof(RoomParticipantText));
            RoomState = "房间已结束，可继续单人播放。";
            OnPropertyChanged(nameof(CanControl));
            OnPropertyChanged(nameof(IsInRoom));
            OnPropertyChanged(nameof(RoomRoleText));
        });
    }

    private void UpdateRoomMemberCount(WatchRoomSnapshot snapshot)
    {
        var count = snapshot.MemberCount > 0
            ? snapshot.MemberCount
            : (snapshot.Host?.Online == true ? 1 : 0)
              + (snapshot.Guest?.Online == true ? 1 : 0);
        if (_roomMemberCount != count)
        {
            _roomMemberCount = count;
            OnPropertyChanged(nameof(RoomParticipantText));
        }
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
            if (_roomPlayer != null)
            {
                await _roomPlayer.SetPausedAsync(true, _lifetime.Token).ConfigureAwait(true);
            }

            if (ServiceAddress == _quickTunnel.ServiceAddress && !_quickTunnel.IsRunning)
            {
                InviteText = string.Empty;
                RoomState = "Cloudflare 地址已失效；请重新创建房间并分享新邀请。";
                return;
            }

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt), _lifetime.Token).ConfigureAwait(true);
                    var snapshot = await _room.ConnectAsync(_connectAddress, create: false, code,
                        _lifetime.Token).ConfigureAwait(true);
                    _lastVersion = -1;
                    OnPropertyChanged(nameof(IsInRoom));
                    OnPropertyChanged(nameof(RoomRoleText));
                    OnPropertyChanged(nameof(CanControl));
                    if (_roomPlayer != null)
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
                    if (DurationSeconds > 1 && _lastPosition >= DurationSeconds - 3)
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
                    var duration = await player.GetDurationAsync(cancellationToken).ConfigureAwait(true);
                    if (duration > 1)
                    {
                        DurationSeconds = duration;
                    }
                    PositionText = TimeSpan.FromSeconds(_lastPosition).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
                    if (!_seekDragging)
                    {
                        SeekPositionSeconds = Math.Clamp(_lastPosition, 0, DurationSeconds);
                    }
                    var buffering = await player.GetBufferingAsync(cancellationToken).ConfigureAwait(true);
                    if (buffering != _lastBuffering)
                    {
                        _lastBuffering = buffering;
                        Status = buffering ? "播放器正在缓冲音视频…" : "音视频缓冲完成。";
                        if (_room.Connected)
                        {
                            await _room.SendAsync(new { type = "buffering", buffering }, cancellationToken)
                                .ConfigureAwait(true);
                        }
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
        var page = _roomPage;
        var browser = _roomBrowser;
        if (page == null || _recoveries >= 2)
        {
            Status = "播放已结束，或网页播放器恢复失败。可重新播放。";
            StopPlayback();
            return;
        }

        _recoveries++;
        Status = "网页播放器连接中断，正在重新打开并恢复进度…";
        try
        {
            if (browser == null)
            {
                throw new InvalidOperationException("网页播放器不可用。");
            }
            StopPlayback();
            var player = await BilibiliWebPlaybackSession.StartAsync(browser, page,
                startPaused: true, cancellationToken).ConfigureAwait(true);
            _roomPlayer = player;
            await player.SeekAsync(_lastPosition, cancellationToken).ConfigureAwait(true);
            if (_lastSnapshot != null && _room.Connected)
            {
                await CorrectPlaybackAsync(_lastSnapshot, cancellationToken).ConfigureAwait(true);
            }
            else
            {
                await player.SetPausedAsync(false, cancellationToken).ConfigureAwait(true);
            }

            Status = "已恢复播放。";
        }
        catch (Exception error) when (IsRoutineError(error))
        {
            _resumeEpisodeId = page.EpisodeId;
            _resumeAtSeconds = _lastPosition;
            Status = "网页播放器恢复失败；若登录已过期，请重新扫码后继续。";
        }
    }

    private void StopPlayback()
    {
        PlayerReady = false;
        PlayerOpacity = 0;
        _roomPlayer?.Dispose();
        _roomPlayer = null;
        _lastBuffering = false;
    }

    private static bool IsRoutineError(Exception error)
    {
        return error is InvalidOperationException or ArgumentException or FormatException
            or System.IO.IOException or UnauthorizedAccessException
            or System.Security.Cryptography.CryptographicException
            or System.Net.Http.HttpRequestException
            or WebSocketException or System.ComponentModel.Win32Exception
            or Newtonsoft.Json.JsonException or System.Text.Json.JsonException
            or System.Runtime.InteropServices.COMException or ObjectDisposedException;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(true);
            await _monitorTask.ConfigureAwait(true);
            if (_loginCancellation != null)
            {
                await _loginCancellation.CancelAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            _loginCancellation?.Dispose();
            _room.SnapshotReceived -= OnSnapshotReceived;
            _room.Disconnected -= OnRoomDisconnected;
            _room.Closed -= OnRoomClosed;
            try
            {
                await _room.DisposeAsync().ConfigureAwait(true);
            }
            finally
            {
                try
                {
                    await _quickTunnel.DisposeAsync().ConfigureAwait(true);
                }
                finally
                {
                    try
                    {
                        await StopLocalServerAsync().ConfigureAwait(true);
                    }
                    finally
                    {
                        if (_roomBrowser != null)
                        {
                            _roomBrowser.WebMessageReceived -= OnWebMessageReceived;
                        }
                        StopPlayback();
                        LoginQrCode?.Dispose();
                        LoginQrCode = null;
                        _loginQrUri = null;
                        _snapshotGate.Dispose();
                        _lifetime.Dispose();
                    }
                }
            }
        }
    }

    private async Task StopLocalServerAsync()
    {
        if (_localServer != null)
        {
            var server = _localServer;
            _localServer = null;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await server.StopAsync(deadline.Token).ConfigureAwait(true);
            }
            finally
            {
                await server.DisposeAsync().ConfigureAwait(true);
            }
        }
    }
}
