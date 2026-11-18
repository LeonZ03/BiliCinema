## 下载与更新

- Windows x64：`BiliCinema-{{VERSION}}-win-x64.exe`，播放需要 Microsoft Edge WebView2 Runtime。
- Android 8+：`BiliCinema-{{VERSION}}-android-universal.apk`，沿用原签名，可覆盖安装并保留设置。
- 两个安装包均附于本 Release，使用 `SHA256SUMS.txt` 校验。

Windows 可在应用内检查 GitHub Release 更新；Android 1.6.13 起通过 BiliCinema 的 Cloudflare 域名检查并下载更新。Windows 1.5.0 及更早版本请手动下载，Android 旧版可覆盖安装。

## 本版更新

- 房主与访客各自控制本机弹幕，互不影响；Android 记住本机弹幕偏好。
- Android 发送后保留聊天输入焦点，新消息滚动不打断输入；键盘出现时视频位置、尺寸保持固定。
- Android 全屏聊天更加透明，点外部只收起聊天；下一次点击视频才显示播放控制栏。
- Android 房间由带通知栏入口的前台服务保持连接，切换应用和界面重建时复用连接。通知栏可直接返回或退出房间；后台暂停本机视频，返回后跟随房间时间轴。
- Windows 单按 Alt 呼出／收起聊天，打开后直接输入；按住不重复切换，组合键不触发。

Android 不提供影片下载。房间最多 5 人，房主控制播放时间轴；账号播放权限与本机弹幕、画质等偏好各自独立。

[使用说明](https://github.com/LeonZ03/BiliCinema#readme) · [Android 说明](https://github.com/LeonZ03/BiliCinema/blob/main/android/README.md)
