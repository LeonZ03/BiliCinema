## 下载与更新

Windows x64 下载 `BiliCinema-{{VERSION}}-win-x64.exe`；Android 8+ 下载 `BiliCinema-{{VERSION}}-android-universal.apk`。
所有文件可通过 `SHA256SUMS.txt` 校验。Windows 播放依赖 WebView2；Android 使用原生 Media3 播放器，登录页面需要可用的 Android System WebView。

## 本版更新

- Android 登录成功后仅显示简单登录状态，自动收起官方网页，不再展示 B 站首页。
- 默认支持扫码登录及复制二维码图片，切换到聊天应用后可粘贴；等待确认、过期与网络失败均有明确提示。
- 登录信息保存后重新读取验证；退出页面不会放弃已开始的保存批次，旧请求和网页回调不能覆盖新尝试。
- Android 建房、入房和恢复连接显示真实步骤与用时；失败保留中断位置，可重试或取消，状态同步完成后才进入房间。
- 双端继续支持版本提示、校验和应用内更新，Android 沿用原发布签名。

Windows 1.5.0 及更早版本请手动下载本版；之后支持带版本号安装包的应用内更新。Android 使用原签名，可覆盖安装或从应用内更新。

房间继续运行在 Cloudflare，最多 5 人，各设备独立从 B 站播放。Android 原生播放器支持授权的 H.264/AAC DASH 与单段 MP4；DRM、预览片段及未支持的编码或分段格式会明确提示。实际扫码、会员内容和系统覆盖更新仍需对应账号与真机验证。

Android 使用说明和实际设备验收要求见 [Android 文档](https://github.com/LeonZ03/BiliCinema/blob/main/android/README.md)，其他操作见 [README](https://github.com/LeonZ03/BiliCinema#readme)。
