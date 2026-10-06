# Verification And Rollback

## 快速狀態

```powershell
git status --short --branch
git rev-parse HEAD
pwsh ./script/audit-module-boundaries.ps1 `
  -OutputPath artifacts/architecture/module-boundary-audit.json
```

Audit JSON 記錄 commit SHA、source metrics 與目前 boundary markers，供 Agent 或 reviewer 看到真實狀態。

## 嚴格驗證

依序執行，不要在同一工作樹平行跑 build/test：

```powershell
dotnet restore ./DownKyi.sln

dotnet build ./DownKyi.sln `
  -c Release `
  --no-restore `
  --no-incremental `
  -p:EnableNETAnalyzers=true `
  -p:AnalysisMode=All `
  -p:EnforceCodeStyleInBuild=true `
  -p:TreatWarningsAsErrors=true `
  -p:CodeAnalysisTreatWarningsAsErrors=true `
  -p:UseSharedCompilation=false

pwsh ./script/test-solution.ps1 -Configuration Release -NoRestore -NoBuild
pwsh ./script/verify-format.ps1
pwsh ./script/audit-module-boundaries.ps1 `
  -OutputPath ./artifacts/architecture/module-boundary-audit.json
$workflowFiles = Get-ChildItem ./.github/workflows -Filter *.yml | `
  Select-Object -ExpandProperty FullName
go run github.com/rhysd/actionlint/cmd/actionlint@v1.7.12 -- $workflowFiles
git diff --check
dotnet package list --project ./DownKyi.sln --vulnerable --include-transitive
dotnet package list --project ./DownKyi.sln --deprecated --include-transitive
pwsh ./script/scan-secrets.ps1
```

`scan-secrets.ps1` 使用 Gitleaks 掃描目前 tracked 與尚未追蹤、但未被 `.gitignore` 排除的候選提交檔。固定驗證版本為 Gitleaks `8.30.1`；Windows x64 release zip 必須先依官方 `gitleaks_8.30.1_checksums.txt` 驗證 SHA-256，再解壓到 `.tools/gitleaks/bin/`。`.gitleaks.toml` 只允許公開 WBI 測試 fixture 與精確的 Avalonia brush resource 行，不得加入整個目錄或一般測試檔的寬鬆排除。

格式驗證由 `verify-format.ps1` 檢查全方案的空白、using 與 code style。
分析器仍全部由上述嚴格建置執行：`dotnet format` 的 analyzer pass
[未正確套用 diagnostic suppressors](https://github.com/dotnet/sdk/issues/51364)，
會將 xUnit 必須公開的測試類等誤報為錯誤。不要因此降低規則嚴重性或移除分析器。

Repository 測試只能經由 `script/test-project.ps1` 或
`script/test-solution.ps1` 進入 `DownKyi.CentralTestRunner`。Runner 會套用
project/platform allowlist、canonical invocation、必要的 in-process xUnit
routing、TRX validation 與 exit result。

正常 PASS 不保留 flight-recorder evidence。FAIL、cancellation 或 abnormal cleanup
會在 `artifacts/test-flight-recorder` 保存 slice/root process identity、事件、
bounded stdout/stderr 與一次 best-effort final process snapshot；child 未被
觀察到不代表已證明不存在。詳細 locator 見 `docs/testing/README.md`。

## Windows x64 單文件程序

從 repository 根目錄執行建置腳本。它會下載並驗證清單鎖定的 aria2、FFmpeg 與
cloudflared，再產生自包含的 Windows x64 單一執行檔：

```powershell
pwsh ./script/build-bilicinema.ps1
Test-Path ./artifacts/BiliCinema-win-x64/BiliCinema.exe
Get-ChildItem ./artifacts/BiliCinema-win-x64 -File
```

`script/assets/external-assets.json` 是外部工具版本與 SHA-256 的唯一清單。
建置會拒絕雜湊不符的下載，並確認輸出目錄中只有 `BiliCinema.exe`。
`.github/workflows/quality.yml` 目前僅在 pull request 時執行格式、嚴格建置、
測試與套件稽核；它不會產生或上傳發行檔案。交付內容是上述 exe，本機建置不需要
建立壓縮檔或 macOS 安裝包。

`BiliCinema Release` 工作流在 main 的 `version.txt` 或工作流本身變更時執行，
也可手動執行。它對同一提交完成格式、嚴格建置、Windows 完整回歸、CodeQL、
單文件打包與實際 aria2 TLS 測試後，才建立 `bilicinema-v<version>` Release，
上傳 `BiliCinema.exe` 與 SHA-256 校驗檔。現有 Release 不會被覆寫。
一般 main 程式碼提交仍不會觸發 Strict PR CI。

登入態 API audit 只能由明確授權的 operator 執行：

```powershell
pwsh ./script/audit-bilibili-authenticated-api.ps1 `
  -ConfirmAuthenticatedLive `
  -OutputPath ./artifacts/bilibili/authenticated-live.json
```

腳本只從 `~/.codex/.env` 讀取 `BILIBILI_TEST_COOKIE`，不得把值放入命令列、檔案、log、fixture、commit 或 PR。`/x/web-interface/nav` 未同時滿足 code 0 與 `isLogin=true` 時，後續 probe 必須封鎖。

## UI 與 runtime evidence

- Real Host/XAML：`UiSmokeTests`。
- Navigation history：typed navigation tests，必須驗證 instance reuse、dispose 與 history shrink。
- Download/retry：loopback fake HTTP tests，不連正式 Bilibili。
- Media output：ffprobe seek/decode integration tests。
- Logs：使用測試指定隔離目錄，檢查 redaction、flush、rotation 與 export。
- System performance：依 `../performance-baseline.md` 記錄 runtime、OS、architecture、dataset、backend 與 SHA。

## 工作目录清理

保留源码、有效测试、许可证、正式文档及构建清单。旧版设计预览、一次性恢复发布脚本
和已解决问题的本地试验产物可以删除；仍有效的交互约定与决策先迁入对应文档。
原始 Logo 定稿属于正式资源，不能作为试验稿清除。

`artifacts/` 中的旧 exe、浏览器试验资料和临时证据不参与源码构建。清理前确认相关
进程已退出，逐一检查目标路径与链接，避免递归到仓库以外。保留仍未解决失败的
取证文件，以及当前需要的工具和验证结果。用户实际账号、下载和设置目录不在
项目清理范围内。发布与保留规则见 [Release Policy](release-policy.md)。

## 回滾

一般 PR 使用非破壞性 revert：

```powershell
git revert <commit-sha>
```

不得用 `git reset --hard` 或覆蓋使用者工作樹。

資料 migration PR 必須在合併前提供：

1. 舊 schema fixture。
2. migration 後 reopen 測試。
3. 備份位置。
4. rollback 或向前修復步驟。
5. 未完成下載與 resume state 驗證。

XAML/rename PR 回滾時應 revert 整個 rename commit，避免只還原 class 而留下 resource URI、DI 或 route references。
