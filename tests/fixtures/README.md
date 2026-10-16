# 測試用 LOG（虛構）

這裡的 LOG 全部是**人工編造的範例**，依 `docs/log-format-notes.md` 描述的共通格式撰寫；站點、設備、命令編號都是虛構的。

真實 LOG 不得放進這裡（CLAUDE.md 第 13 條、ADR-0009）。

| 資料夾 | 內容 |
|---|---|
| `one-day-plc` | 單日、單一模組：同時間戳記、時間倒退、延續行 |
| `two-modules` | 單日、兩個模組：跨模組的同時間戳記排序 |
| `parsing-cases` | 解析狀況（ADR-0004）：PLC 為 Big5（含無法解碼的位元組、檔案開頭的前置內容、無效時間、輪替檔與跨檔案的時間倒退）；Server 為 UTF-8（stack trace 延續行、未知種類的 Logger、整個檔案無法解析的 `notes.txt`） |
| `multi-day` | 兩天（`two-modules` 的 20300107 與 `parsing-cases` 的 20300108），外面多包一層 `logs\` 資料夾 |
| `multi-day.zip`、`multi-day.7z` | 與 `multi-day` 資料夾內容相同的壓縮檔（以 7-Zip 建立），用來確認三種來源的匯入結果相同 |
| `fields-cases` | 解析欄位（ADR-0005 第 8 點）：站點、搬送機、對接設備、命令、棧板、桶、訊號、警報、連線、模式、儲位、命令類型、來源與目的地；格式為虛構，非初始規則涵蓋的欄位搭配測試中的自訂規則 |
