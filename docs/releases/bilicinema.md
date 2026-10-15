## 下载与更新

Windows x64 下载 `BiliCinema.exe`；Android 8+ 下载 `BiliCinema-1.5.0-android-universal.apk`。
所有文件可通过 `SHA256SUMS.txt` 校验。Windows 播放依赖 WebView2，Android 播放依赖 Android System WebView。

## 本版更新

- 房间状态运行在 Cloudflare Durable Objects，Windows、安卓手机和平板直接连接同一域名；不再需要常开电脑或 Tunnel。
- 最多 5 人，房主统一控制影片与进度；支持缓冲等待、重连与聊天。视频仍由各设备从 B 站独立播放。
- Windows 登录遇到 DNS、TLS、代理、协议不支持或超时时显示错误提示。
- Windows 和 Android 检查本仓库的新版本并弹出更新提示。下载后校验 SHA256；Windows 保存任务并退出替换，Android 校验同包同签名后打开系统安装器。

首次从旧 Windows 版升级请手动下载本版；后续版本可在应用内更新。房主离线 2 分钟后结束房间；主动结束后邀请立即失效。会员内容权限取决于各自 B 站账号。

Android 使用说明和实际设备验收要求见 [Android 文档](https://github.com/LeonZ03/BiliCinema/blob/main/android/README.md)，其他操作见 [README](https://github.com/LeonZ03/BiliCinema#readme)。
