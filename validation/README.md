# 技術驗證（#15）

驗證 ADR-0007（用戶端技術）與 ADR-0011（儲存與索引）的候選方案是否可行。這裡的程式是**驗證用**，不是正式產品；之後的骨架（#16）可以參考或沿用其中的做法。

> 所有測試資料都由 `TechValidation.DataGen` 產生，是**虛構**的資料（IP 使用文件專用網段 192.0.2.0/24，電話、命令、站點、設備都是編造的）。真實 LOG 不得放進這裡，也不得把真實 LOG 的內容寫進結果（CLAUDE.md 第 13 條、ADR-0009）。

## 專案

| 專案 | 用途 | 對應項目 |
|---|---|---|
| `src/TechValidation.Core` | 解析、正規化、匯入、SQLite 儲存與搜尋、狀態快照（簡化版） | ADR-0004、ADR-0010、ADR-0011 |
| `src/TechValidation.DataGen` | 虛構 LOG 產生器（「日期 → 模組」結構、Big5／UTF-8、延續行、輪替檔） | T-02 |
| `src/TechValidation.Bench` | 匯入、索引大小、搜尋、正規表示式、串連、快照、排序、可攜性的量測，並與 DuckDB、Lucene.NET 比較 | T-03～T-13 |
| `src/TechValidation.UiSpike` | WPF＋AvalonDock：10 萬筆事件清單、時間軸、2D 畫布、版面儲存與還原；自我檢查 | ADR-0007 第 1～4 項、T-01 |
| `tests/TechValidation.Tests` | 正規化（T-13）與解析規則的單元測試 | — |

## 需要

- .NET 10 SDK（開發與建置）。執行 UiSpike 的發佈版**不需要**安裝 .NET。

## 執行

```powershell
cd validation

# 單元測試
dotnet test

# 1. 產生虛構資料（預設 7 天、每天 500 MB，約 3.5 GB）
dotnet run -c Release --project src/TechValidation.DataGen -- --out D:\tv-data\logs

# 2. 量測（需要約 30 GB 磁碟空間），報告寫到 results\
dotnet run -c Release --project src/TechValidation.Bench -- --logs D:\tv-data\logs --work D:\tv-data\work --report results\bench-<電腦名稱>.md

# 3. 冷快取：重新開機後，沿用已匯入的案件只跑查詢
dotnet run -c Release --project src/TechValidation.Bench -- --logs D:\tv-data\logs --work D:\tv-data\work --reuse --skip-compare --cold --report results\bench-<電腦名稱>-cold.md

# 4. UI 驗證：發佈 self-contained 單一資料夾
dotnet publish src/TechValidation.UiSpike -c Release -o publish\UiSpike
publish\UiSpike\TechValidation.UiSpike.exe --selftest results\uispike-<電腦名稱>.json --exit
```

資料與工作資料夾請放在 repo **以外**的位置。

## 需要手動確認的項目

自動量測無法取代的部分，請在對應的電腦上操作並把結果記到 `results\`。工程師電腦上用發佈版 LogTrace 的步驟見 [`engineer-pc-checklist.md`](engineer-pc-checklist.md)（#47 A 類）。

| 項目 | 做法 |
|---|---|
| 乾淨、離線的 Windows 10（ADR-0007 第 3 項、T-01） | 在沒有安裝 .NET、拔掉網路的 Windows 10 上，複製 `publish\UiSpike` 整個資料夾，執行 `TechValidation.UiSpike.exe --selftest result.json`。確認程式能開啟，且 `self_contained=true`、`fts5_trigram_ok=true`、`nfkc_ok=true` |
| 捲動、縮放、拖曳的體感（ADR-0007 第 1、4 項） | 在 UiSpike 中用滑鼠捲動事件清單、拖曳捲軸、用滾輪縮放時間軸與畫布、拖曳畫布上的元素。自我檢查以「p95 格時間 ≤ 33 ms（約 30 fps）」為**建議**門檻，最後以實際操作的感受判斷 |
| 版面（ADR-0007 第 2 項） | 拖動面板、浮動時間軸 →「儲存版面」→ 關閉程式 → 重開 →「載入版面」 |
| 複製案件到另一台電腦（T-12） | 把 Bench 產生的 `case-1day-parallel` 資料夾複製到另一台電腦，以 Bench 的 `--reuse` 執行，比對查詢結果 |
| 不同 Windows 版本的正規化（T-13） | 在其他版本的 Windows 執行 UiSpike 自我檢查，比對 `normalization_cases_passed` 與 `icu` |
| 目標硬體（T-14） | 確認工程師電腦的磁碟類型與記憶體；若與開發電腦差異大，在該電腦重跑 Bench |
