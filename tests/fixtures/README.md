# 測試用 LOG（虛構）

這裡的 LOG 全部是**人工編造的範例**，依 `docs/log-format-notes.md` 描述的共通格式撰寫；站點、設備、命令編號都是虛構的。

真實 LOG 不得放進這裡（CLAUDE.md 第 13 條、ADR-0009）。

| 資料夾 | 內容 |
|---|---|
| `one-day-plc` | 單日、單一模組：同時間戳記、時間倒退、延續行 |
| `two-modules` | 單日、兩個模組：跨模組的同時間戳記排序 |
| `parsing-cases` | 解析狀況（ADR-0004）：PLC 為 Big5（含無法解碼的位元組、檔案開頭的前置內容、無效時間、輪替檔與跨檔案的時間倒退）；Server 為 UTF-8（stack trace 延續行、未知種類的 Logger、整個檔案無法解析的 `notes.txt`） |
