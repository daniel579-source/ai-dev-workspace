# 自動化倉儲設備 LOG 回溯系統

*Automated Warehouse Equipment Log Traceability System（英文名稱暫定）*

## 專案目的

本系統供公司內部工程師使用，以**時間**為核心回溯自動化倉儲設備的事件，用來：

- 還原設備異常的發生順序；
- 查明不同設備之間的關聯；
- 縮短故障排查時間。

主要設計目標：以時間為基準，盡可能重現當時發生的情況。

## 目前開發階段

> **第一階段已實作完成，進行驗證與確認**：規格見 issue #14，實作 ticket 為 #15～#29 及後續的子 issue。技術驗證（#15）的程式與結果在 `validation/`。尚待工程師電腦量測與現場 LOG 樣本的項目由 #47 追蹤；需求與決策的對照見 `docs/requirements.md`（v0.2）。

## 建置與執行

需要 .NET 10 SDK。

```powershell
dotnet build LogTrace.slnx
dotnet test LogTrace.slnx                                   # 含 FlaUI 冒煙測試，會開啟視窗
                                                             # 執行前先關閉桌面上的 LogTrace，否則焦點被搶走，鍵盤與滑鼠的測試會失敗
dotnet test LogTrace.slnx --filter "Category!=UI"           # 只跑不開視窗的測試
dotnet run --project src/LogTrace.App                        # 案件（Cases\）與廠別設定檔（Plants\）預設放在 D:\LogTrace，設定與版面在 %LOCALAPPDATA%\LogTrace
dotnet run --project src/LogTrace.App -- --data-root D:/temp/lt  # 設定、版面、案件、廠別設定都改用這個資料夾（不影響自己的資料，也不詢問搬移）

# 效能量測（資料量大，不在 repo；未設定環境變數時直接結束）
#   LOGTRACE_PERF_SOURCE：一天份匯入；LOGTRACE_PERF_SEARCH_SOURCE：全文索引與搜尋（一天或多天的資料夾）
$env:LOGTRACE_PERF_SOURCE = "D:/_case/tv-data/logs/20300107"; $env:LOGTRACE_PERF_WORK = "D:/_case/tv-data/work"
$env:LOGTRACE_PERF_SEARCH_SOURCE = "D:/_case/tv-data/logs"
dotnet test tests/LogTrace.Engine.Tests -c Release --filter "Category=Perf" --logger "console;verbosity=detailed"

# 打包：self-contained 單一資料夾＋zip（ADR-0007），目標電腦不需要安裝 .NET；
# 含使用說明（docs/user-guide.md）、第三方元件清單（THIRD-PARTY-NOTICES.md）與授權全文（licenses/）
pwsh scripts/package.ps1                                     # → publish/LogTrace-<版本>-win-x64.zip

# 以冒煙測試驗證發佈版（zip 解壓後的 LogTrace.exe）；LOGTRACE_E2E_SOURCE 另以一天份資料在畫面上走主要流程
$env:LOGTRACE_APP_EXE = "D:/temp/LogTrace/LogTrace.exe"; $env:LOGTRACE_E2E_SOURCE = "D:/_case/tv-data/logs/20300107"
dotnet test tests/LogTrace.App.SmokeTests --logger "console;verbosity=detailed"
```

| 專案 | 內容 |
|---|---|
| `src/LogTrace.Engine` | 案件引擎：無畫面的函式庫（案件管理、匯入資料夾／7z／zip、解析、排序與事件序號、儲存、廠別設定檔的讀寫與檢查）。7z 使用 7-Zip 的 `7za.dll`（LGPL，隨程式放在 `7z\`） |
| `src/LogTrace.Workspace` | ViewModel（主畫面、Layout 編輯器）：不依賴 WPF 控制項 |
| `src/LogTrace.App` | WPF 畫面（AvalonDock） |
| `tests/` | 引擎測試、ViewModel 測試、FlaUI 冒煙測試；測試用 LOG 在 `tests/fixtures`（虛構） |

## 文件導覽

| 文件 | 內容 |
|---|---|
| [CLAUDE.md](CLAUDE.md) | AI 協作規則與專案工作原則 |
| [docs/requirements.md](docs/requirements.md) | 需求規格：背景、目標、FR／NFR、使用流程、驗收條件、待確認問題 |
| [docs/architecture.md](docs/architecture.md) | 架構評估：資料流程、候選職責、候選架構方案、待決策事項 |
| [docs/log-format-notes.md](docs/log-format-notes.md) | LOG 格式調查紀錄模板（每種設備或 LOG 來源各填一份） |
| [docs/user-guide.md](docs/user-guide.md) | 使用說明（隨發佈版附上） |
| `docs/adr/` | 架構決策的個別詳細紀錄（ADR）；索引與現行結論見 `docs/architecture.md` 第 10 章 |

## 第一階段功能範圍

1. 依時間、設備、關鍵字等條件搜尋 LOG。
2. 將多台設備的事件整合到共同時間軸。
3. 查看指定時間區間內發生的事件。
4. 協助工程師重建特定時間的事件順序與設備狀態。

已知的資料特性：LOG 來源是設備或電腦產生的檔案，預估每天約 100 MB 至 10 GB。

## 技術選型

已定案的決策見 `docs/architecture.md` 第 10 章的決策索引（ADR-0001～ADR-0012 已接受；ADR-0013 知識庫與本機離線 LLM 為提議中，不在第一階段），例如：

- **部署形態**：單機桌面程式（免安裝、離線、Windows 10／11 64 位元），見 ADR-0001。
- **用戶端**：C# + .NET 10 + WPF + AvalonDock，self-contained 單一資料夾發佈，見 ADR-0007（已接受；事件清單的人工體感待確認，見 #47）。
- **儲存與索引**：SQLite 為主的混合方案，見 ADR-0011（已接受；開發電腦實測達標，工程師電腦與冷快取待量測，見 #47）。

## 如何參與需求確認

1. 第一階段的待確認問題已回答（見 `docs/requirements.md` 第 10 節）；目前尚待確認的項目集中在 #47。
2. 若能提供 LOG 樣本，請依 `docs/log-format-notes.md` 模板填寫一份調查紀錄。
   - **請先去識別化**，也不要把真實 LOG 上傳到未經核准的外部服務。
3. 對需求或架構有意見時，請直接在對應文件標註，或與專案負責人討論，並註明問題編號（如 `Q-003`、`FR-005`）。
4. 確認後的事項會由「待確認」改為「已確認」，並記錄確認日期與確認者。
