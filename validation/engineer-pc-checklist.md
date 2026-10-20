# 工程師電腦的量測步驟（#47 A 類）

在工程師的筆電上，用**發佈版 LogTrace** 實際操作，量測 ADR-0007、ADR-0010、ADR-0011 中標示「待實測」的項目。工程師電腦**不需要安裝任何東西**。

- 開發電腦的數字來自量測測試（`dotnet test --filter Category=Perf`），這份清單改用畫面操作，兩者不完全相同。要直接比較時，可以在開發電腦上照同樣步驟做一次。
- 測試資料一律用**虛構資料**（技術驗證產生的 `tv-data`）。結果可以寫進 repo。
- 如果另外用真實 LOG 試，只記秒數與筆數，不記內容（ADR-0009、CLAUDE.md 第 13 條）。
- 結果記到 `validation/results/engineer-pc-<電腦名稱>.md`，格式見最後一節。

## 0. 準備（在開發電腦上）

| 項目 | 來源 | 大小 |
|---|---|---|
| 發佈版 | `publish/LogTrace-<版本>-win-x64.zip`（`pwsh scripts/package.ps1` 產生） | 約 60 MB |
| 虛構 LOG 七天份 | `tv-data/logs`（`20300107`～`20300113`，每天約 500 MB） | 3.5 GB |
| （選用）一天 1.5 GB | 在開發電腦上產生：`dotnet run -c Release --project validation/src/TechValidation.DataGen -- --days 1 --mb-per-day 1500 --start 2030-01-07 --out <輸出資料夾>` | 1.5 GB |
| 正規化自我檢查（第 7 步用） | `tv-data/tv-uispike.zip`（技術驗證的 UiSpike 發佈版，免安裝） | 59 MB |

用隨身碟或外接硬碟帶到工程師電腦。工程師電腦要有約 **25 GB** 的可用空間：七天份案件約 14 GB，加上原始 LOG 與一天份案件。

## 1. 記錄電腦規格（ADR-0011 T-14）

在 PowerShell 執行，把輸出抄到結果檔：

```powershell
Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, BuildNumber
Get-CimInstance Win32_Processor | Select-Object Name, NumberOfCores, NumberOfLogicalProcessors
"{0:N1} GB" -f ((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB)
Get-PhysicalDisk | Select-Object FriendlyName, MediaType, BusType, @{n='GB';e={[int]($_.Size/1GB)}}
Get-Volume | Where-Object DriveLetter | Select-Object DriveLetter, FileSystemLabel, @{n='FreeGB';e={[int]($_.SizeRemaining/1GB)}}
```

另外記下：虛構 LOG 與量測用資料放在哪一槽、那一槽是 SSD 還是硬碟；以及量測時是否接電源。筆電用電池時可能降頻。

## 2. 啟動程式（不影響工程師自己的資料）

1. 把發佈版 zip 解壓到例如 `D:\Tools\LogTrace-perf`。
2. 把虛構 LOG 複製到例如 `D:\perf\logs`。
3. 用獨立的資料夾啟動，量測用的案件與設定就不會混進 `D:\LogTrace`：

   ```powershell
   D:\Tools\LogTrace-perf\LogTrace.exe --data-root D:\perf\lt
   ```

4. 關閉其他耗資源的程式。量測過程中，桌面上只開這個 LogTrace。

## 3. 一天份匯入（ADR-0011 第 4 點：匯入到可以瀏覽，平常量級 2 分鐘內）

1. 建立案件「perf-1day」，廠別用「內建預設」。獨立資料夾裡沒有廠別設定，所以預設就是它；量測不需要 Layout。
2. 在「匯入 LOG」選 `D:\perf\logs\20300107`。按「匯入」的同時用**碼表**開始計時。
3. 事件清單出現事件、可以捲動時停錶，記為「**可以瀏覽**」。
4. 抄下狀態列的匯入訊息：檔案數、事件數、「耗時 N 秒」。
5. 用碼表記錄「全文索引：已建立」出現的時間（從按下「匯入」算起）。
6. （選用）用一天 1.5 GB 的資料再做一次，案件名稱「perf-1500」。最大量級的目標是 3 分鐘。

## 4. 一天份搜尋（ADR-0010 第 5 點：一般關鍵字 2 秒內）

等「全文索引：已建立」出現後，在 perf-1day 依序搜尋下表。每次抄下搜尋狀態列的「符合筆數、秒數、方式（全文索引／掃描）」。

| # | 輸入框 | 內容 | 其他設定 |
|---|---|---|---|
| 1 | 全部包含 | `卸貨` | |
| 2 | 全部包含 | `CMD_NO` | |
| 3 | 全部包含 | `00010927` | |
| 4 | 全部包含 | `卸貨 A-17` | |
| 5 | 全部包含 | `00010927 CMD_NO` | |
| 6 | 任一包含 | `出庫循環 卸貨完成` | |
| 7 | 全部包含／不包含 | `CMD_NO`／`卸貨` | |
| 8 | 全部包含 | `SubmitCompleted` | 勾選區分大小寫 |
| 9 | 全部包含 | `不存在的虛構字串` | |
| 10 | 全部包含 | `CMD_NO=0001092\d` | 勾選正規表示式（不設目標） |

開發電腦的筆數可以對照 `validation/results/search-21-dev-pc.md`。資料相同時，筆數應完全相同。

## 5. 修改時間偏移量（參考，目前沒有目標）

1. 在 perf-1day 按「時間校正…」，把整體偏移量設為 `1`，按確定的同時開始計時，畫面可以操作時停錶。
2. 改回 `0`，再量一次。

## 6. 事件清單的人工體感（ADR-0007 第 1 項）

在 perf-1day（約 98 萬筆事件）操作，每項記「順／可接受／不順」，加上一句說明：

- 用滑鼠滾輪連續捲動；拖曳捲軸從頭拉到尾。
- 點選事件，看詳細資訊與原始 LOG 跟上的速度。
- 按住 ↓ 連續移動；按 →、Shift＋→、Ctrl＋→ 各十幾次。開發電腦的自動化量測是方向鍵每步約 0.3 秒。
- 切換混合檢視與分軌檢視。
- 從選單切換「回放」、「搜尋」兩種版面。

有任何一項「不順」，ADR-0007 的對策是改用自行繪製的清單。這要另開 ticket 討論。

## 7. 七天份與冷快取（ADR-0010 第 5 點：七天 10 秒內；ADR-0011 第 6 點）

1. 建立案件「perf-7day」（內建預設），匯入 `D:\perf\logs`（七個日期資料夾一起匯入）。用碼表記錄可以瀏覽與「全文索引：已建立」的時間。
2. 依第 4 步的表格搜尋，這是**熱快取**的結果。
3. 關閉 LogTrace，**重新開機**。開機後先不開其他程式。
4. 用第 2 步的指令啟動 LogTrace，開啟 perf-7day。用碼表記錄從按「開啟」到可以瀏覽的時間。
5. 立刻搜尋第 1 項 `卸貨`，這是**冷快取的第一次搜尋**。接著依序做第 2～10 項，最後再搜尋一次第 1 項做比較。

## 8. 可攜性（ADR-0011 T-12）

需要第二台有 LogTrace 的電腦，開發電腦也可以。

1. 在 LogTrace 關閉案件（或關閉程式），把 `D:\perf\lt\Cases\` 底下的 perf-1day 資料夾整個複製到另一台電腦的案件存放位置。
2. 在另一台電腦開啟這個案件，搜尋第 4 步的第 1～3 項。筆數要和原電腦相同。
3. 關閉案件，刪除案件資料夾裡的 `index\`。重新開啟，等「全文索引：已建立」，再搜尋同樣三項。筆數要相同，方式應為「全文索引」。

## 9. 其他 Windows 版本的正規化（ADR-0011 T-13）

在 Windows 版本與開發電腦（Windows 11）不同的電腦上做。工程師電腦如果是 Windows 10，就在這台做。

1. 在 perf-1day 搜尋 `ＣＭＤ＿ＮＯ`（全形）與 `cmd_no`（小寫），筆數都要和第 4 步第 2 項 `CMD_NO` 相同。
2. 把 `tv-uispike.zip` 解壓，執行：

   ```powershell
   .\TechValidation.UiSpike.exe --selftest D:\perf\uispike-<電腦名稱>.json --exit
   ```

   確認結果中 `nfkc_ok=true`、`fts5_trigram_ok=true`，並記下 `normalization_cases_passed` 與 `icu`。開發電腦的值見 `validation/results/uispike-dev-pc.json`。

## 10. 結束

- 量測用資料可以整個刪除：`D:\perf`、`D:\Tools\LogTrace-perf`。工程師自己的 `D:\LogTrace` 沒有被用到。
- 把結果交回後，由 Claude 依結果更新 ADR-0007、ADR-0011 的狀態、`docs/architecture.md` 與 #47。

## 結果記錄格式

把下面的表格複製到 `validation/results/engineer-pc-<電腦名稱>.md` 填寫：

```markdown
# 工程師電腦量測（#47 A 類）

- 日期：
- 量測者：
- 電腦：作業系統／處理器（核心／執行緒）／記憶體／磁碟（類型、匯流排）／資料所在槽／是否接電源
- 程式版本：LogTrace-<版本>

| 項目 | 目標 | 結果 | 備註 |
|---|---|---|---|
| 一天份：可以瀏覽（碼表） | 2 分鐘 | | |
| 一天份：匯入訊息的耗時 | — | | 檔案數、事件數 |
| 一天份：全文索引已建立（碼表） | — | | |
| 一天 1.5 GB：可以瀏覽（選用） | 3 分鐘 | | |
| 修改偏移量 0→1、1→0 | — | | |
| 七天份：可以瀏覽（碼表） | — | | |
| 七天份：全文索引已建立（碼表） | — | | |
| 冷快取：開啟七天份案件到可以瀏覽 | — | | |
| 冷快取：第一次搜尋「卸貨」 | 10 秒 | | |

搜尋（筆數／秒數／方式）：

| # | 一天份 | 七天份（熱） | 七天份（冷，重開機後） |
|---|---|---|---|
| 1 | | | |
| … | | | |
| 10 | | | |

事件清單體感：滾輪／拖曳捲軸／點選／按住 ↓／→ 系列／混合與分軌／切換版面，各記「順／可接受／不順」與說明。

可攜性：複製後三項搜尋筆數相同（是／否）；刪除 index\ 重建後相同（是／否）。

正規化：全形、小寫搜尋筆數相同（是／否）；UiSpike：nfkc_ok、fts5_trigram_ok、normalization_cases_passed、icu。
```
