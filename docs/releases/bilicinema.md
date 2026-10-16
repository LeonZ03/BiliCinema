## 下载与更新

Windows x64 下载 `BiliCinema-{{VERSION}}-win-x64.exe`；Android 8+ 下载 `BiliCinema-{{VERSION}}-android-universal.apk`。
所有文件可通过 `SHA256SUMS.txt` 校验。Windows 播放依赖 WebView2；Android 使用原生 Media3 播放器，登录页面需要可用的 Android System WebView。

## 本版更新

- Android 全面重写为原生首页、播放器与聊天界面，支持手机横屏全屏和大屏分栏，播放时不再显示完整 B 站网页。
- 房间由房主控制播放，访客独立加载和追赶；访客缓冲或短暂离线不再暂停全房。房主加载或缓冲时仍统一等待。
- 全屏点击视频右侧空白区域打开聊天，点击抽屉外关闭并保留草稿；新消息在右侧显示昵称与内容。
- 修复 Windows 播放器初始化、恢复与延迟事件回声导致的同步干扰。
- 网络、登录、格式或系统播放限制显示可恢复提示；双端继续支持版本提示、校验和应用内更新。
- 修复 Android 页面重建、换片与退出期间网络清理可能导致的闪退；视频结束后可重新播放。

Windows 1.5.0 及更早版本请手动下载本版；之后支持带版本号安装包的应用内更新。Android 使用原签名，可覆盖安装或从应用内更新。

房间继续运行在 Cloudflare，最多 5 人，各设备独立从 B 站播放。Android 原生播放器支持授权的 H.264/AAC DASH 与单段 MP4；DRM、预览片段及未支持的编码或分段格式会明确提示。实际扫码、会员内容和系统覆盖更新仍需对应账号与真机验证。

Android 使用说明和实际设备验收要求见 [Android 文档](https://github.com/LeonZ03/BiliCinema/blob/main/android/README.md)，其他操作见 [README](https://github.com/LeonZ03/BiliCinema#readme)。
