# BiliCinema

BiliCinema 基于 [DownKyi Core](https://github.com/crazysmile-PhD/downkyicore) 开发，保留原项目的影片下载能力，并提供 B 站网页播放与双人同步观影。Windows 主界面分为登录、观影房间、下载影片三页；观影房间无需创建或加入房间也能单人播放。

## 直接运行

双击 `artifacts/BiliCinema-win-x64/BiliCinema.exe`。它是 Windows x64 自包含单文件程序；目标电脑无需 .NET SDK，也不用另开房间服务窗口。播放 B 站网页视频需要系统具备 Microsoft Edge WebView2 Runtime。此目录是本地构建产物，不提交到 Git，也不发布压缩包。

程序首次启动时会准备内置下载器；进入下载页或创建房间时会按需准备其他媒体工具或 Cloudflare Tunnel 客户端。登录页可把二维码连同 BiliCinema 标识复制为图片，方便私下发送给帮忙扫码的人。观影房间支持 B 站番剧、电影和普通 BV/av 视频，视频播放走本机 WebView2 与 B 站；房主可在同一个房间切换视频，房间服务传选片、播放控制和聊天文字。聊天昵称保存在本机设置中。下载页解析影片后会按所选清晰度、编码和音质显示每项预计大小。

当前使用和房间同步说明见 [观影使用说明](docs/watch-together.md)。

## 构建

在 Windows x64 开发电脑双击 `Build-BiliCinema.cmd`。构建需要 .NET 10 SDK，并会下载仓库清单中锁定哈希的 aria2、FFmpeg，以及锁定版本和哈希的 cloudflared。完成后只交付 `artifacts/BiliCinema-win-x64/BiliCinema.exe`。程序源码可运行 `dotnet run --project DownKyi/DownKyi.csproj`。

默认数据目录沿用 DownKyi 的 `%APPDATA%\DownKyi`。可设置 `DOWNKYI_DATA_DIR` 隔离测试账号。下载器仍使用原项目的配置、断点续传和媒体处理逻辑。

## 仓库

开发文档位于 [ARCHITECTURE.md](ARCHITECTURE.md)、[docs/maintenance.md](docs/maintenance.md) 和 [测试说明](docs/testing/README.md)。许可和第三方归属见 [LICENSE](LICENSE) 与 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
