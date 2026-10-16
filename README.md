# 自動化倉儲設備 LOG 回溯系統

*Automated Warehouse Equipment Log Traceability System（英文名稱暫定）*

## 專案目的

本系統供公司內部工程師使用，以**時間**為核心回溯自動化倉儲設備的事件，用來：

- 還原設備異常的發生順序；
- 查明不同設備之間的關聯；
- 縮短故障排查時間。

主要設計目標：以時間為基準，盡可能重現當時發生的情況。

## 目前開發階段

> **技術驗證與第一階段實作**：規格見 issue #14，實作 ticket 為 #15～#29。技術驗證（#15）的程式與結果在 `validation/`。

## 建置與執行

需要 .NET 10 SDK。

```powershell
dotnet build LogTrace.slnx
dotnet test LogTrace.slnx                                   # 含 FlaUI 冒煙測試，會開啟視窗
dotnet test LogTrace.slnx --filter "Category!=UI"           # 只跑不開視窗的測試
dotnet run --project src/LogTrace.App                        # 案件預設放在 %LOCALAPPDATA%\LogTrace\Cases

# 發佈：self-contained 單一資料夾（ADR-0007），目標電腦不需要安裝 .NET
dotnet publish src/LogTrace.App -c Release -r win-x64 --self-contained -o publish/LogTrace
```

| 專案 | 內容 |
|---|---|
| `src/LogTrace.Engine` | 案件引擎：無畫面的函式庫（匯入、解析、排序與事件序號、儲存） |
| `src/LogTrace.Workspace` | 回放工作區 ViewModel：不依賴 WPF 控制項 |
| `src/LogTrace.App` | WPF 畫面（AvalonDock） |
| `tests/` | 引擎測試、ViewModel 測試、FlaUI 冒煙測試；測試用 LOG 在 `tests/fixtures`（虛構） |

## 文件導覽

| 文件 | 內容 |
|---|---|
| [CLAUDE.md](CLAUDE.md) | AI 協作規則與專案工作原則 |
| [docs/requirements.md](docs/requirements.md) | 需求規格：背景、目標、FR／NFR、使用流程、驗收條件、待確認問題 |
| [docs/architecture.md](docs/architecture.md) | 架構評估：資料流程、候選職責、候選架構方案、待決策事項 |
| [docs/log-format-notes.md](docs/log-format-notes.md) | LOG 格式調查紀錄模板（每種設備或 LOG 來源各填一份） |
| `docs/adr/` | 架構決策的個別詳細紀錄（ADR）；索引與現行結論見 `docs/architecture.md` 第 10 章 |

## 第一階段功能範圍

1. 依時間、設備、關鍵字等條件搜尋 LOG。
2. 將多台設備的事件整合到共同時間軸。
3. 查看指定時間區間內發生的事件。
4. 協助工程師重建特定時間的事件順序與設備狀態。

已知的資料特性：LOG 來源是設備或電腦產生的檔案，預估每天約 100 MB 至 10 GB。

## 技術選型

已定案的決策見 `docs/architecture.md` 第 10 章的決策索引（ADR-0001～ADR-0011），例如：

- **部署形態**：單機桌面程式（免安裝、離線、Windows 10／11 64 位元），見 ADR-0001。
- **用戶端**：C# + .NET 10 + WPF + AvalonDock，self-contained 單一資料夾發佈，見 ADR-0007（待技術驗證）。
- **儲存與索引**：SQLite 為主的混合方案，見 ADR-0011（待實測）。

## 如何參與需求確認

1. 閱讀 `docs/requirements.md` 第 10 節的「待確認問題」，依優先順序回覆。
2. 若能提供 LOG 樣本，請依 `docs/log-format-notes.md` 模板填寫一份調查紀錄。
   - **請先去識別化**，也不要把真實 LOG 上傳到未經核准的外部服務。
3. 對需求或架構有意見時，請直接在對應文件標註，或與專案負責人討論，並註明問題編號（如 `Q-003`、`FR-005`）。
4. 確認後的事項會由「待確認」改為「已確認」，並記錄確認日期與確認者。
