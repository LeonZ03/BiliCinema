## 下载与更新

- Windows x64：`BiliCinema-{{VERSION}}-win-x64.exe`，单文件运行，播放需要 Microsoft Edge WebView2 Runtime。
- Android 8+：`BiliCinema-{{VERSION}}-android-universal.apk`，使用原发布签名，可覆盖安装。
- 两个安装包均可通过附件 `SHA256SUMS.txt` 校验。

Windows 1.5.1 起可在应用内检查本仓库 Release 更新；1.5.0 及更早版本需手动下载。Android 1.6.13 起通过 BiliCinema 的 Cloudflare 域名检查并下载更新，不依赖 GitHub；旧版也可手动覆盖安装本版，之后使用应用内更新。安装仍需系统确认。

## 本版更新

- 汇集此前本地测试版本的修复，统一 Windows 和 Android 的首页、账号、观影及聊天界面；自己的昵称和消息靠右。
- 自动处理 B 站网页/App 的整段分享文字、BV/av 与 b23.tv 剧集链接，修复跨端入房后的旧影片残留和访客暂停、黑屏问题。
- Android 登录显示头像、昵称和 UID；支持画质、选集、弹幕及字幕。非全屏控制栏精简为画质与倍速，全屏操作合并为一行。
- Android 点击全屏按影片比例选择方向，支持翻转 180 度；开关聊天和键盘保持视频布局稳定。
- Windows 下载页与观影共用登录状态；修复快速切页卡住、重复初始化及代理干扰本机下载引擎通信。精简重复头像、按钮和弹窗。
- Android 更新使用 Cloudflare 同域安装包，继续检查摘要、包名、版本及发布签名；保留现有登录和设置。

房间继续运行在 Cloudflare，最多 5 人，视频由各设备直接从 B 站播放。房主控制房间时间轴，访客缓冲不会暂停其他人。内容权限取决于各自账号；Android 的受保护影片使用内嵌官方播放器，官方网页与 App 的授权和设备支持可能不同。Android 不提供影片下载。

使用说明：[项目主页](https://github.com/LeonZ03/BiliCinema#readme) · [Android 文档](https://github.com/LeonZ03/BiliCinema/blob/main/android/README.md)
