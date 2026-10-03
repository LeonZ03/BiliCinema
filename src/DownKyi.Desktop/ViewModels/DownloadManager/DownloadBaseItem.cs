using System;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.Zone;
using DownKyi.Models;
using DownKyi.Utils;

namespace DownKyi.ViewModels.DownloadManager
{
    internal class DownloadBaseItem : ObservableObject
    {
        // model数据
        private DownloadBase _downloadBase = new();

        public DownloadBase DownloadBase
        {
            get => _downloadBase;
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                _downloadBase = value;

                ZoneImage = DictionaryResource.GetIfApplicationInitialized<DrawingImage>(
                    VideoZoneIcon.GetZoneImageKey(DownloadBase.ZoneId));
                OnPropertyChanged();
                OnPropertyChanged(nameof(Order));
                OnPropertyChanged(nameof(MainTitle));
                OnPropertyChanged(nameof(Name));
                OnPropertyChanged(nameof(Duration));
                OnPropertyChanged(nameof(VideoCodecName));
                OnPropertyChanged(nameof(Resolution));
                OnPropertyChanged(nameof(AudioCodec));
                OnPropertyChanged(nameof(FileSize));
            }
        }

        // 视频分区image
        private DrawingImage? _zoneImage;

        public DrawingImage? ZoneImage
        {
            get => _zoneImage;
            set => SetProperty(ref _zoneImage, value);
        }

        // 视频序号
        public int Order
        {
            get => DownloadBase.Order;
            set
            {
                DownloadBase.Order = value;
                OnPropertyChanged();
            }
        }

        // 视频主标题
        public string MainTitle
        {
            get => DownloadBase.MainTitle;
            set
            {
                DownloadBase.MainTitle = value;
                OnPropertyChanged();
            }
        }

        // 视频标题
        public string Name
        {
            get => DownloadBase.Name;
            set
            {
                DownloadBase.Name = value;
                OnPropertyChanged();
            }
        }

        // 时长
        public string Duration
        {
            get => DownloadBase.Duration;
            set
            {
                DownloadBase.Duration = value;
                OnPropertyChanged();
            }
        }

        // 视频编码名称，AVC、HEVC
        public string VideoCodecName
        {
            get => DownloadBase.VideoCodecName;
            set
            {
                DownloadBase.VideoCodecName = value;
                OnPropertyChanged();
            }
        }

        // 视频画质
        public Quality Resolution
        {
            get => DownloadBase.Resolution;
            set
            {
                DownloadBase.Resolution = value;
                OnPropertyChanged();
            }
        }

        // 音频编码
        public Quality AudioCodec
        {
            get => DownloadBase.AudioCodec;
            set
            {
                DownloadBase.AudioCodec = value;
                OnPropertyChanged();
            }
        }

        // 文件大小
        public string? FileSize
        {
            get => DownloadBase.FileSize;
            set
            {
                DownloadBase.FileSize = value;
                OnPropertyChanged();
            }
        }
    }
}
