using System;
using CommunityToolkit.Mvvm.ComponentModel;
using DownKyi.Images;
using DownKyi.Presentation;

namespace DownKyi.ViewModels.UiState;

internal enum VideoDetailDisplayState
{
    Idle,
    Busy,
    Content,
    Empty
}

internal sealed partial class VideoDetailUiState : ObservableObject
{
    public event Action? IsBusyChanged;

    [ObservableProperty]
    private string? _inputText;

    [ObservableProperty]
    private string _inputSearchText = string.Empty;

    [ObservableProperty]
    private VectorImage _downloadManage = ButtonIcon.DownloadManage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsContentVisible))]
    private VideoInfoView? _videoInfoView;

    [ObservableProperty]
    private bool _isSelectAll;

    [ObservableProperty]
    private int _gridResetVersion;

    [ObservableProperty]
    private VideoPage? _selectedVideoPage;

    [ObservableProperty]
    private string _onlinePlaybackInfo = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(IsContentVisible))]
    [NotifyPropertyChangedFor(nameof(IsEmptyVisible))]
    private VideoDetailDisplayState _displayState;

    public bool IsBusy => DisplayState == VideoDetailDisplayState.Busy;

    public bool IsContentVisible => DisplayState == VideoDetailDisplayState.Content
                                    || (DisplayState == VideoDetailDisplayState.Busy && VideoInfoView != null);

    public bool IsEmptyVisible => DisplayState == VideoDetailDisplayState.Empty;

    partial void OnDisplayStateChanged(VideoDetailDisplayState oldValue, VideoDetailDisplayState newValue)
    {
        if ((oldValue == VideoDetailDisplayState.Busy) != (newValue == VideoDetailDisplayState.Busy))
        {
            IsBusyChanged?.Invoke();
        }
    }
}
