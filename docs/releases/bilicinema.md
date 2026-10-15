## 下载与更新

Windows x64 下载 `BiliCinema-{{VERSION}}-win-x64.exe`；Android 8+ 下载 `BiliCinema-{{VERSION}}-android-universal.apk`。
所有文件可通过 `SHA256SUMS.txt` 校验。Windows 播放依赖 WebView2，Android 播放依赖 Android System WebView。

## 本版更新

- Windows 安装包和 Release 文件名包含版本号与平台，双端构建产物按版本和平台归档。
- Windows 更新器识别带版本号的新包，校验来源、大小和 SHA-256；下载或启动失败时保留、恢复原程序。
- 清理已过时的上游发布脚本和旧构建包，统一 Windows 构建入口命名。

Windows 1.5.0 及更早版本请手动下载本版；之后支持带版本号安装包的应用内更新。Android 使用原签名，可覆盖安装或从应用内更新。

房间继续运行在 Cloudflare，最多 5 人，各设备独立从 B 站播放。Android 使用 WebView，实际扫码、会员播放、横竖屏与覆盖更新仍需真机验证。

Android 使用说明和实际设备验收要求见 [Android 文档](https://github.com/LeonZ03/BiliCinema/blob/main/android/README.md)，其他操作见 [README](https://github.com/LeonZ03/BiliCinema#readme)。
