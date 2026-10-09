# BiliCinema 固定域名路由部署

这部分部署只操作 `bilicinema.leonz03.dpdns.org` 和
`bilicinema-h-<session>.leonz03.dpdns.org`，不修改 Whisper 的 Worker、D1、
Durable Objects 或 `whisper.leonz03.dpdns.org`。

## 一次性准备

确认 `leonz03.dpdns.org` 是当前 Cloudflare 账户的 Active Zone，并准备：

- 账户 ID；
- Zone ID；
- 仅包含 Cloudflare Tunnel Edit 和该 Zone DNS Edit 的 API Token。

API Token 只配置为 Worker Secret：

```powershell
cd cloud
npx wrangler login
npm install
npx wrangler secret put CLOUDFLARE_API_TOKEN
npx wrangler secret put CLOUDFLARE_ACCOUNT_ID
npx wrangler secret put CLOUDFLARE_ZONE_ID
npx wrangler deploy
```

部署前先用 `npx wrangler dev` 和本地合成 Tunnel 测试路由、过期清理和
WebSocket 代理。不要把 `.dev.vars`、Token、Tunnel token 或测试 URL 提交到 Git。

## 运行时行为

- Worker 为每个活跃主机会话创建一个远程 Tunnel；同一主机上的多个房间共用它。
- Tunnel 的 DNS 记录使用 `bilicinema-h-<session>.leonz03.dpdns.org`，保持一级子域名以使用免费证书，不会放进邀请地址。
- 房间邀请使用 `wss://bilicinema.leonz03.dpdns.org/ws?room=<room-code>`。
- 主机每 20 秒发送心跳；60 秒后新加入请求进入离线状态，120 秒后删除房间映射、DNS 记录和 Tunnel。
- 显式结束房间会立即删除该房间路由；删除失败由 Durable Object alarm 和 Worker cron 重试。
- 创建资源前先持久记录主机会话；部分创建失败或删除中断时保留清理记录，逐项删除后再移除会话。
- Tunnel 主机名保持一级子域名；`global_fetch_strictly_public` 让同一区域的转发走正常公网入口。
- WebSocket 两端显式设置 `binaryType="arraybuffer"`，保留文本及二进制消息内容。
- 客户端等待 Tunnel 注册后再验证入口；管理请求的连接复用时间有上限，避免一次早期失败锁住后续探测。公网邀请还会验证实际 WebSocket 升级。
- 短暂网络超时不会停止心跳任务；云端会话已过期时，房主用同一本机房间重新申请通道并注册路由。结束或退出可取消正在恢复的通道。
- WebSocket 异常断开时将保留关闭码转换为可发送的关闭码，释放另一端连接，避免空探测连接悬挂。
- Windows 将 Tunnel 子进程绑定到主程序生命周期，强制结束 exe 时也会终止子进程。云端清理会先撤销旧 Tunnel 凭据、断开残留连接，再删除 Tunnel，避免残留连接反复重连阻止回收。
- 同一次申请重试复用原会话，不重复消耗创建限额。并发会话上限与限流参数以 `wrangler.jsonc` 为准；实际可用量还受账户共享免费额度限制。

云端尚未配置或运行时令牌权限不足时，exe 仍可以创建本地房间，但公网邀请准备会失败并保留本地房间供重试。正常使用者只需安装 exe，无须登录 Cloudflare。
