# Testing

測試分層：

- `DownKyi.Domain.Tests`：state transitions 與 value objects。
- `DownKyi.Application.Tests`：commands、queries、coordinators 與 ports。
- `DownKyi.Infrastructure.Tests`：SQLite、migration、write-behind 與 adapters。
- `DownKyi.Core.Tests`：Bilibili contracts、HTTP、settings、logging、FFmpeg、aria2。
- `DownKyi.Desktop.Tests`：real Host、XAML 與 typed navigation smoke tests。
- `DownKyi.Tests`：executable compatibility 與 end-to-end service tests。
- `DownKyi.Architecture.Tests`：重要 dependency direction 與 repository wiring。
- `DownKyi.Windows.Tests`：Windows process、Job Object、native handle 與播放器瀏覽器佈局行為。
- `DownKyi.Linux.Tests`：Linux process、signal 與 descendant lifecycle 行為。
- `DownKyi.MacOS.Tests`：macOS system Bash、signing 與 packaging 行為。

## Formal Test Entry

`tools/DownKyi.CentralTestRunner` 是 repository 正式 test execution entry。
`script/test-project.ps1` 與 `script/test-solution.ps1` 經由
`script/test-project-runner.ps1` 呼叫它。Runner 擁有：

- 對 `tests` 下所有 `*.Tests.csproj` 的自動 discovery（排除 `bin`/`obj`）；
- 每個 discovered project 無條件宣告的 `DownKyiTestPlatforms` platform selection；
- canonical invocation 與 slice/test identity；
- `docs/testing/test-runner-policy.json` 中必要的 xUnit in-process routing exceptions；
- per-project TRX validation 與 target exit result。

`test-runner-policy.json` 不是 test-project registry 或 allowlist。新增 test project 會被自動發現，必須宣告 `DownKyiTestPlatforms`；只有需要偏離預設 VSTest 路由的專案才加入 policy exception。

正式 PowerShell boundary 每次先 build CentralTestRunner，再執行目前
repository state 的 runner。不要直接新增平行的 `dotnet test` / `vstest`
repository entry。

## 房间网络验证

房间网络验证使用完整类名 `DownKyi.Desktop.Tests.RoomTransportTests`：默认仅跑本机
WebSocket 握手、关闭重建和网关拒绝场景。在线 Cloudflare 场景仅在
`BILICINEMA_LIVE_TUNNEL_TEST=1` 时运行，CI 默认跳过。在线验证需要空闲的 5077 端口，
以及构建清单中的 cloudflared 位于 PATH（源码构建缓存为
`src/DownKyi.Desktop/EmbeddedTools`）；它创建两条临时隧道并验证访客加入，结束时释放
服务和进程，不使用 B 站账号。

```powershell
pwsh ./script/test-project.ps1 -ProjectPath tests/DownKyi.Desktop.Tests/DownKyi.Desktop.Tests.csproj `
  -ClassName DownKyi.Desktop.Tests.RoomTransportTests -Configuration Release -NoRestore -NoBuild
```

## Lightweight Flight Recorder

CentralTestRunner 從 test process 啟動時記錄 slice identity、root PID 與
可取得時的 start time，以及 exit、exit code、cancellation、bounded stop、
cleanup 和 bounded stdout/stderr tail。正常 PASS 會刪除 recorder evidence。

FAIL、cancellation 或 abnormal cleanup 會保存 evidence，並取得一次 failure-time
best-effort process snapshot。Child rows 只表示當下觀察到的 PID、PPID 與
start time；沒有觀察到 child 不能解讀成證明 child 不存在。Recorder 是
diagnostic aid，不判斷 root cause、PrimaryFailure、causal precedence 或完整
descendant history。

Focused recorder behavior 位於
`tests/DownKyi.Architecture.Tests/CentralTestRunnerRecorderTests.cs`：一個
deterministic cancellation fixture 證明失敗 evidence，另一路徑確認 PASS 不保留
大型 evidence。

短暫 sharing violation、resource busy、rename/move/overwrite 或 database-lock
失敗使用 [Targeted Resource Forensics](targeted-resource-forensics.md)。先對準
resource 與真實 operation，再用相同語義的 probe 找 failure window；只有直接
owner/lifecycle evidence 才能宣稱 root cause proven。不得先 blanket-enable
tracing、重跑相同失敗或加入 timing workaround。

## Test Isolation

`BilibiliPlayerSurfaceTests` 使用已安裝的 Microsoft Edge 無介面模式，執行離線頁面
及正式播放器注入腳本，驗證小窗模式恢復、播放器替換、控制欄可點擊及播放狀態保留；
操作提示用例實際執行聊天層腳本，確認既有／新播放器、視窗／全屏下逐行顯示及獨立到期。
此用例不需要 Playwright 套件或 B 站登入；瀏覽器使用獨立臨時 profile，並由 Job Object
管理與清理。Windows 測試環境須安裝 Edge。

測試不得讀取使用者真實 settings、cookie、下載 DB 或 aria2 session。網路
contract tests 使用 fixture 或 loopback server。OS-specific behavioral tests
必須位於對應 platform project；Architecture tests 不模擬另一個作業系統。

其他 authority locator：

- module dependency policy：`module-boundary-ratchets.md`
- dependency、binary 與 release maintenance：`../maintenance.md`
- formal verification 與 rollback：`../operations/verification-and-rollback.md`
