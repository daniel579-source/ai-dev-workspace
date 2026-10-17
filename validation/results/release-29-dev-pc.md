# 發佈與端對端驗收（#29，開發用電腦）

- 日期：2026-10-16
- 對應：#29 發佈與端對端驗收；ADR-0001（單機、免安裝、完全離線）、ADR-0007（self-contained 單一資料夾）。
- 發佈版：`pwsh scripts/package.ps1` 產生的 `LogTrace-0.1.0-win-x64.zip`，**解壓到新的資料夾後**測試。
- 資料：全部是**虛構**資料（`tests/fixtures`；大量資料為技術驗證產生的 `tv-data\logs`）。
- 電腦：與 `summary-dev-pc.md` 相同（16 執行緒、64 GB 記憶體、NVMe SSD；開發用電腦，**已安裝 .NET SDK**）。

## 1. 打包

| 項目 | 結果 |
|---|---|
| zip | 61 MB；解壓後 286 個檔案 |
| self-contained | `LogTrace.runtimeconfig.json` 為 `includedFrameworks`（Microsoft.NETCore.App、Microsoft.WindowsDesktop.App 10.0.13），不依賴電腦上安裝的 .NET；未啟用 InvariantGlobalization（全形半形正規化需要，ADR-0011） |
| 原生 DLL 的相依 | `7za.dll`：OLEAUT32、USER32、ADVAPI32、msvcrt、KERNEL32；`e_sqlite3.dll`：KERNEL32；`LogTrace.exe`：系統 DLL 與 UCRT（`api-ms-win-crt-*`）。全部是 Windows 10 內建的元件 |
| 附件 | `使用說明.md`、`THIRD-PARTY-NOTICES.md`、`licenses\`（11 個授權檔）、`7z\License.txt`；打包腳本會確認這些檔案都在，且**不含** unRAR 授權限制的 `7z.dll` |

## 2. 冒煙測試（FlaUI）

16 個冒煙測試，**建置輸出**與**zip 解壓後的發佈版**（`LOGTRACE_APP_EXE`）都全部通過。涵蓋規格要求的關鍵流程：

| 流程 | 測試 |
|---|---|
| 建立案件並匯入虛構 LOG（資料夾、7z）→ 事件清單、原始 LOG | `ImportSmokeTests` |
| 搜尋 → 點結果 → 方向鍵 → Layout 與事件清單同步 | `MomentSmokeTests` |
| Layout 編輯器建立元素並存檔 | `LayoutEditorSmokeTests` |
| 其他：電話遮罩、回放與元素狀態、命令與設備狀態表、未對應拖曳成為別名、廠別設定新版、時間校正、版面保存與還原、全文索引狀態與正規表示式、離線 | 其餘 12 個 |

## 3. 執行期間沒有對外連線

- `OfflineSmokeTests`：啟動 → 建立案件與匯入（含背景建立全文索引）→ 搜尋 → 播放 → 開啟 Layout 編輯器，每個階段以 `netstat -ano` 檢查程式擁有的 TCP／UDP 端點：**沒有任何端點**（建置輸出與發佈版都通過）。
- 程式碼中沒有使用網路的 API（`HttpClient`、`WebRequest`、`Socket`、`Dns`、`WebView` 等）。

## 4. 以一天份資料在畫面上走主要流程（發佈版）

`LargeDataSmokeTests`，`tv-data\logs\20300107`（502 MB、98.5 萬筆事件），內建預設的廠別設定：

| 步驟 | 時間 |
|---|---|
| 匯入到可以瀏覽（含讀出所有事件與建立畫面） | 40.9 秒（目標 2 分鐘） |
| 搜尋罕見詞（索引建立中，以掃描） | 1.4 秒（其中搜尋 1.06 秒） |
| 點一筆結果到所有面板同步 | 0.21 秒 |
| 方向鍵 → 5 次 | 每次 0.28～0.32 秒（含 UI 自動化每 0.1 秒輪詢的延遲） |
| 全文索引建好後再搜尋 | 0.05 秒（全文索引） |

### 過程中發現並修正的問題

第一次量測時，每按一次方向鍵約 **1.4 秒**、點一筆結果 1.5 秒。在 ViewModel 層逐項計時後，時間軸、Layout 推導都是 0 ms，慢在「未對應」清單（#26）每次查詢目前時刻前後 30 秒的編號：SQLite 選用了「種類」的索引，等於掃過整個案件的解析欄位。改為指定以事件序號的索引查詢後，ViewModel 層每一步 **20～40 ms**。小量測試資料看不出這個問題。

## 5. 其他量測

- 修改時間偏移量（#19 待實測）：一天份（98.5 萬筆）重新解析並排序 **32 秒**；七天份依此推估約 4 分鐘（`ImportPerformanceTests`）。
- 一天份基本匯入 34.6 秒；搜尋 0.21～0.36 秒；案件大小 3.67 倍（未建全文索引時）。全文索引與七天份見 `search-21-dev-pc.md`。

## 6. 規格的主要使用流程

以虛構資料走過規格（#14）與需求文件（`docs/requirements.md` 第 3.2 節）的主要情境：

| 情境 | 走法 | 結果 |
|---|---|---|
| UC-1 異常時間點回溯 | 點時間軸跳到某時刻；方向鍵走過前後的狀態變化；設備狀態表只看異常 | 通過（`MomentSmokeTests`、`CommandsSmokeTests`、`LargeDataSmokeTests`） |
| UC-2 關鍵字／錯誤碼搜尋 | 三個輸入框、篩選、正規表示式；結果依時間排序，時間軸標出位置 | 通過（`SearchSmokeTests`、`SearchIndexSmokeTests`；索引與掃描結果相同見 #21） |
| UC-3 跨設備關聯追蹤 | 選命令列出串起來的跨模組事件；依 Layout 元素（含別名）篩選 | 通過（`CommandsSmokeTests`、`ParsedFieldsSmokeTests`） |
| UC-4 狀態重建 | Layout 回放、元素狀態、設備狀態表在任一時刻的狀態 | 通過（`PlaybackSmokeTests`、`MomentSmokeTests`） |
| UC-5 回到原始紀錄 | 選事件，原始 LOG 面板依原始行序標出該行；詳細資訊有來源檔案、行號、原始時間 | 通過（`ImportSmokeTests`） |
| 廠別設定與 Layout | 編輯器建立元素並存檔；把未對應的編號拖成別名；新版設定提示並套用 | 通過（`LayoutEditorSmokeTests`、`ParsedFieldsSmokeTests`、`PlantSnapshotSmokeTests`） |
| 時間校正 | 設定模組偏移量，頂端標示已校正，事件重新排序 | 通過（`TimeCorrectionSmokeTests`） |
| 版面 | 兩種預設、保存、還原、版面檔損壞 | 通過（`DockLayoutSmokeTests`） |

## 7. 待確認／待實測

- **乾淨、離線的 Windows 10**：~~待確認~~ → **2026-10-16 通過**。使用者以 `LogTrace-0.1.0-win-x64.zip`（`main` 的 `bab8b8e`，SHA-256 `3239E629…A76D`），在沒有安裝 .NET、沒有網路的 Windows 10 實機上依下列步驟確認：
  1. 把 zip 複製到該電腦，解壓到任一資料夾。
  2. 執行 `LogTrace.exe`，確認視窗出現、右上角顯示時區。
  3. 建立案件，匯入一份虛構 LOG（例如 repo 的 `tests\fixtures\multi-day.7z`），確認事件清單出現、可以搜尋與用方向鍵移動。（實機確認時匯入的是 `multi-day.7z`，因此也確認了 ADR-0012 的 7z 解開。）
  4. 開啟 Layout 編輯器，新增一個站點並存檔。
  5. 關閉程式，確認 `%LOCALAPPDATA%\LogTrace` 下有案件與設定。
- 以下項目已移到 #47 追蹤：
- 冷快取與工程師電腦上的效能（#15、#20、#21 相同）。
- 公司備份是否涵蓋 `%LOCALAPPDATA%`（使用說明已提醒自行備份）。
- 冒煙測試偶發失敗：`DockLayoutSmokeTests` 在完整測試中失敗過兩次（#19、#29 開發期間），單獨與重跑都無法重現。推測是選單點選後程式尚未處理就被關閉，已讓測試等程式處理完輸入；之後若再出現，要保留錯誤訊息。
