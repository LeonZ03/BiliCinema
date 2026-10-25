# Cloudflare 云端房间部署

生产入口为 `wss://bilicinema.leonz03.dpdns.org/v2/ws`，Worker 名称仍为
`bilicinema-router`。本配置只拥有 BiliCinema 的 Worker、Durable Objects 和
既有域名，不应修改其他应用。

## 本地验证与部署

使用 Node.js 24，在 `cloud/` 执行：

```powershell
npm ci
npm test
npx wrangler deploy --dry-run
npx wrangler login
npx wrangler deploy
```

Android APK 使用同一 Worker 的 `/downloads/` 静态资源，清单为
`/downloads/android-manifest.json`。Cloudflare 的单个静态文件上限为 25 MiB。
签名构建后，在仓库根目录执行：

```powershell
pwsh -File script/prepare-android-cloud-update.ps1 -ApkPath artifacts/releases/<version>/android/BiliCinema-<version>-android-universal.apk
cd cloud
npm test
npx wrangler deploy --dry-run
npx wrangler deploy
```

准备脚本先从正式域名恢复并校验旧清单及 APK，再把新签名 APK、实际字节数、
SHA-256 与版本码写入静态资源；清单保留当前及两个历史版本。不得用不同字节
覆盖同一版本文件名。正式部署后检查清单、APK 响应与哈希，并在已安装旧版的
设备上确认更新。APK 和生成的清单留在本地暂存目录，不提交 Git。

CI 保留自动部署。每次自动部署前先从正式域名读回当前清单及其中的全部 APK，
逐个验证大小和 SHA-256 后恢复到 assets 目录，任何异常阻止部署。首次尚无
清单时仅在现有 Worker `/health` 正常时允许空资源部署。若从另一台机器发版，
同样先运行准备脚本；不要直接以空 assets 目录执行 Wrangler 部署。

检查 `/health` 返回 `ok: true`、`version: "2"`；再做真实 WSS 建房、加入、
结束及旧邀请失效检查。HTTP health 不代表 WebSocket 协议已通过。
SQLite Durable Objects 可用免费方案；实际可用性受账户共享额度限制。
`CLOUD_CREATE_RATE_LIMIT` 默认每来源 10 分钟 10 次，`CLOUD_DAILY_ROOM_LIMIT`
默认整个服务每天 512 次建房尝试。公开服务遭到限流或额度耗尽时应显示错误并稍后重试。

`wrangler.jsonc` 的 `router-v1` 迁移保持不变，`router-v2` 添加 CloudRoom。
不要删除仍有旧客户端使用的 registry、旧迁移或旧管理 secrets。新房间不创建
Tunnel/DNS，只使用 ROOMS 绑定；原 registry 继续负责旧资源清理和建房限流。

## GitHub 自动部署

`.github/workflows/cloud-rooms.yml` 在 cloud 目录变更时运行真实运行时测试和
Wrangler dry-run，main 通过后部署到既有域名。仓库/环境 secrets 需配置：

- `CLOUDFLARE_API_TOKEN`：仅部署该 Worker、Durable Objects 及既有域名所需权限。
- `CLOUDFLARE_ACCOUNT_ID`：对应账户。

不要复制本机短期 OAuth token 到 CI；使用适当范围的 CI API token。不得把 secrets
放入源码、日志、PR 或聊天。生产部署 job 使用 `cloudflare-production` environment。

也可在 Cloudflare Worker Settings → Builds 连接 `LeonZ03/BiliCinema`，生产分支
main，root directory 为 `cloud`，build command 为 `npm ci && npm test`，deploy
command 为 `npx wrangler deploy`。选择 GitHub Actions 或 Workers Builds 一个部署
owner，避免两条流水线并发部署。若采用 Workers Builds，应停用 Actions deploy job。

## 回滚

使用 Worker deployment history 或 `npx wrangler rollback` 回滚代码。涉及新增 DO
类的首次迁移不能当成普通无状态回滚；保留 binding 和迁移历史，优先修复或回滚到
同一迁移之后的兼容版本。不要删除房间存储或替换旧迁移来强行回滚。

## 客户端发布身份

Windows 继续使用 `bilicinema-v<version>` Release；Android 使用上述 Cloudflare
同域静态 APK 和 manifest 更新。发版工作流先验证 .NET、CodeQL、Windows 单文件和
Android 单测/lint/签名；APK 的 Cloudflare 发布按上述本地步骤单独完成。
Android CI 使用 `ANDROID_KEYSTORE_BASE64`、`ANDROID_KEY_ALIAS`、
`ANDROID_KEYSTORE_PASSWORD`、`ANDROID_KEY_PASSWORD`；必须导入首次发布所用的同一个
签名身份，不能每次重新生成。密钥仅在 runner 临时目录解码，打包后删除。
