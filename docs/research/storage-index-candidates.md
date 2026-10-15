# 研究：儲存與索引候選方案比較

| 項目 | 內容 |
|---|---|
| 文件狀態 | 研究紀錄 v0.1（**未決定**；所有技術一律為**候選方案**，由使用者決定） |
| 日期 | 2026-10-16 |
| 對應 ticket | #11「儲存與索引候選方案比較」（Wayfinder research） |
| 相關決策 | ADR-0001、ADR-0002、ADR-0003、ADR-0004、ADR-0005、ADR-0007、ADR-0008、ADR-0009、ADR-0010 |
| 資料來源原則 | 只引用一手來源（官方文件、套件中繼資料、授權條款）；**未讀取、未引用任何真實 LOG**；文中範例一律為「範例／虛構」 |

> **閱讀提示**：本文中「來源」表示有一手來源支持；「估算」表示依假設推算、尚未實測；「待實測」表示必須以原型量測才能確認。
> 本文**不是**架構決策。若使用者採納建議，應另立 ADR 並更新 `docs/architecture.md` 第 10 章索引。

---

## 1. 結論摘要

1. **建議（未決定）**：以 **SQLite（Microsoft.Data.Sqlite）** 作為案件內的主要儲存（事件基本欄位、解析欄位、多值欄位、命令／棧板串連索引、狀態快照、案件中繼資料），全文搜尋採「**正規化文字＋FTS5 trigram 索引加速**，**少於 3 個字的關鍵字與正規表示式改用平行掃描**」的混合做法。
2. 關鍵理由：SQLite 為公有領域、Microsoft 官方提供 .NET 存取套件、預設原生組件已內含 FTS5、單一檔案易於複製；FTS5 trigram 官方支援任意子字串比對，符合 ADR-0010 的「部分比對、不做斷詞」。
3. 最大的已知缺口：**FTS5 trigram 對少於 3 個字（Unicode 字元）的查詢無法使用索引**（來源）。中文兩字詞（例如虛構的「異常」）很常見，這類查詢必須掃描，因此「掃描路徑」不是備案，而是**必要的一部分**。
4. 不分大小寫與全半形相同（NFKC）**任何候選方案都沒有內建**；一律在匯入時由程式（.NET `string.Normalize(NormalizationForm.FormKC)` ＋大小寫摺疊）產生「搜尋用正規化文字」，查詢字串以同一規則處理。
5. 效能目標（一天 500 MB／2 秒、七天／10 秒「開始顯示結果」）在**估算**上可達成，前提是：事件依排序規則預先編號、依日分片並行查詢、結果以時間順序串流顯示。**全部需要實測**（第 8 節）。
6. 案件預設位置**建議（未決定）**：`%LOCALAPPDATA%\<程式名稱>\Cases\`，不建議「文件」資料夾，主因是企業常以 OneDrive「已知資料夾移動」把「文件」同步到雲端（來源），可能讓真實 LOG 離開本機（第 7 節）。

---

## 2. 需求對照基準

以下彙整自 ticket #11 最後一則留言與各 ADR，作為比較的基準。

| 代號 | 需求 | 出處 |
|---|---|---|
| C1 | 單機 Windows 10／11 64 位元、免安裝、完全離線 | ADR-0001 |
| C2 | 可從 .NET 10（C#）使用；能放進 self-contained 單一資料夾；不需另外安裝服務或執行環境 | ADR-0007 |
| C3 | 以免付費元件為主；優先 MIT、Apache 2.0、MS-PL 等允許商業使用的授權 | ADR-0007 |
| C4 | 儲存層可替換 | ADR-0001 |
| D1 | 事件基本欄位（標準化時間、原始時間字串、等級、Logger、模組、訊息全文、原始位置〔檔案、起訖行、位元組位移〕、旗標） | ADR-0004 |
| D2 | 解析欄位（站點、搬送機、對接設備、儲位、命令、棧板、桶〔可多個〕、訊號、站點模式、警報、連線、模式） | ADR-0005 |
| D3 | 穩定識別碼：模組、日期資料夾、檔案相對路徑、起始行號；重新匯入後不變 | ADR-0004 |
| Q1 | 依標準化時間排序讀取，同時間依固定規則（行號、模組順序、Logger） | ADR-0004 |
| Q2 | 部分比對、不分大小寫、全半形相同、比對整筆事件（含延續行） | ADR-0010 |
| Q3 | 正規表示式掃描（可取消、顯示進度） | ADR-0010 |
| Q4 | AND／OR／NOT；依模組、Logger、等級、旗標、解析欄位篩選 | ADR-0010 |
| Q5 | 以命令編號、棧板編號、元素別名快速串連跨模組事件 | ADR-0005、ADR-0008 |
| Q6 | 回放時快速取得任一時刻的世界狀態（例如定期狀態快照） | ADR-0008 |
| P1 | 一般關鍵字：一天份 2 秒內、七天份 10 秒內**開始顯示結果**；正規表示式不設目標 | ADR-0010 |
| P2 | 量級**假設**：一天最多 500 MB、一個案件最多 7 天（待實際資料確認） | ADR-0010 |
| P3 | 匯入時間：尚無目標，需一併評估 | ticket #11 |
| S1 | 案件放在使用者個人資料夾，不放在程式資料夾 | ADR-0007、ADR-0009 |
| S2 | 案件資料夾內容完整，整個複製即為備份；可刪除；程式不加密；稽核紀錄存放在案件中 | ADR-0009 |

### 2.1 量級估算（估算，待實測）

| 項目 | 數值 | 依據 |
|---|---|---|
| 樣本 | 一天約 20 MB、約 2.5～3 萬筆事件 | `docs/log-format-notes.md`（各 Logger 筆數概略加總） |
| 每筆事件平均大小 | 約 0.7 KB | 估算（20 MB ÷ 約 2.8 萬筆；SQL 與 HTTP 內容拉高平均） |
| 假設上限：一天 | 500 MB、約 **70 萬筆**事件（若事件較短可能到 200 萬筆以上） | 估算 |
| 假設上限：一案件 | 7 天、約 3.5 GB 原始文字、約 **500 萬筆**事件 | 估算 |
| 解碼後在記憶體中的大小 | UTF-16 下約為原始位元組的 1～2 倍；7 天可能達 5～7 GB | 估算；**不能假設全部載入記憶體** |

---

## 3. 共通設計要點（與候選方案無關）

下列做法無論選哪個方案都需要，先說明以免在各方案中重複。

### 3.1 搜尋用正規化文字（Q2）

- **事實（來源）**：
  - SQLite 的 `LIKE` 預設只對 ASCII 做大小寫摺疊，非 ASCII 字元區分大小寫（[SQLite 運算式文件](https://www.sqlite.org/lang_expr.html)）。
  - FTS5 的 trigram tokenizer 提供 `case_sensitive`、`remove_diacritics` 選項；文件**沒有**提到 Unicode 相容正規化（NFKC）（[SQLite FTS5 文件](https://www.sqlite.org/fts5.html)）。
  - .NET 提供 `NormalizationForm.FormKC`（完整相容分解後再組合）（[NormalizationForm](https://learn.microsoft.com/en-us/dotnet/api/system.text.normalizationform)）。
  - .NET 在 Windows 上預設使用系統的 `icu.dll`（.NET 7 以上可在 Windows 10 1703 以上載入），無法載入時退回 NLS；也可以改用隨程式附帶的 app-local ICU（[Globalization and ICU](https://learn.microsoft.com/en-us/dotnet/core/extensions/globalization-icu)）。
  - **Globalization Invariant 模式下，正規化不做任何事、大小寫只處理 ASCII**（[Invariant mode 設計文件](https://github.com/dotnet/runtime/blob/main/docs/design/features/globalization-invariant-mode.md)）。
- **做法（候選）**：
  1. 匯入時把整筆事件（含延續行）解碼成文字後，產生一份**搜尋用正規化文字**：NFKC → 大小寫摺疊（例如 `ToLowerInvariant`）。
  2. 查詢字串用**同一個函式**正規化後再比對。
  3. 「區分大小寫」選項：只做 NFKC、不做大小寫摺疊。需要第二份文字或在候選結果上以原文複核（見 3.4）。
  4. 顯示時的醒目標示：NFKC 可能改變字元數（例如部分合字展開），**正規化文字的位置不能直接對應原文**。建議只對畫面上可見的事件，在原文上重新比對並建立字元位置對照（估算成本很低）。
- **限制**：程式**不得**啟用 `InvariantGlobalization`。不同 Windows 版本的系統 ICU 版本不同，同一案件複製到另一台電腦時，查詢字串的正規化結果理論上可能與匯入時不同（影響極少數字元，**待確認**）；如需完全一致，可評估 app-local ICU（`Microsoft.ICU.ICU4C.Runtime` 套件，授權**待確認**）。

### 3.2 排序鍵與事件序號（Q1、D3）

- 匯入完成後，依 ADR-0004 的規則（標準化時間 → 同檔行號／輪替接續 → 模組固定順序 → Logger 名稱）排序，給每筆事件一個**事件序號**（64 位元整數，候選名稱 `seq`）。
- `seq` 是**衍生值**：重新匯入或加入新的一天時可以重編；**穩定識別碼**仍是「模組、日期資料夾、檔案相對路徑、起始行號」（D3），書籤與追溯一律存穩定識別碼。
- 好處：所有查詢結果只要「依 `seq` 遞增」就自動符合排序規則；在 SQLite 中可直接用 `seq` 當 rowid，FTS5 的結果也會依 rowid 遞增輸出，**搜尋結果不需要另外排序**，可以邊找邊顯示。
- 案件偏移量（ADR-0003）對全部事件加同一常數，不改變 `seq`。

### 3.3 依日分片（P1）

- 查詢多半有時間區間，且案件以「天」為單位增加。把可重建的大型衍生資料（全文索引、正規化文字）**依日期分成獨立檔案**（候選），可以：
  - 一天份查詢只碰一個分片；七天份可**平行**查詢 7 個分片，再依 `seq` 合併。
  - 重新匯入某一天時只重建該天。
- 代價：跨日的命令鏈需要查多個分片（以案件層級的串連索引解決，見 3.5）。

### 3.4 搜尋執行策略（Q2、Q3、Q4、P1）

| 查詢類型 | 執行方式（候選） |
|---|---|
| 一般關鍵字，每個詞 ≥ 3 個字 | 用 n-gram／trigram 索引找出候選事件 |
| 有任一詞 < 3 個字（例如中文兩字詞） | 該詞無法用 trigram 索引；若同一個 AND 框中有其他 ≥ 3 字的詞，先用它縮小範圍，否則掃描正規化文字 |
| OR | 各詞結果依 `seq` 合併（聯集） |
| NOT | 從結果中排除；只有 NOT 時等同掃描 |
| 篩選條件（模組、Logger、等級、旗標、解析欄位、時間區間） | 結構化索引；與文字條件取交集 |
| 正規表示式 | 掃描原文（解碼後）；依時間順序分段執行，回報進度、可取消 |
| 區分大小寫 | 先用不分大小寫的索引找候選，再對候選的原文複核 |

- .NET 正規表示式：`RegexOptions.NonBacktracking`（.NET 7 起）保證與輸入長度成線性時間，但不支援反向參考、原子群組、平衡群組、條件式等（[Regular expression options](https://learn.microsoft.com/en-us/dotnet/standard/base-types/regular-expression-options)）。建議：預設使用一般引擎加逾時，或在模式不含上述語法時改用 NonBacktracking（**待決定**，屬於實作細節）。
- 正規表示式一律由 .NET 執行，不交給資料庫的正規表示式引擎，以免不同元件的語法差異（例如 DuckDB 使用 RE2，見 4.4）。

### 3.5 串連索引與多值欄位（D2、Q5）

- 以「參照表」記錄 (種類, 值, 事件序號)：種類例如命令編號、棧板編號、桶編號、站點別名、儲位。一個事件可以有多列（一對多），**桶的多值欄位自然以多列表示**。
- 命令與棧板的串連查詢就是「指定種類與值，依 `seq` 取出所有事件」，只要在 (種類, 值, seq) 上建立索引即可。
- Layout 元素的「所有別名」：查詢時把元素展開成別名清單，以 `IN` 查詢（別名對應屬於廠別設定，改 Layout 不必重建索引；ADR-0005）。

### 3.6 狀態快照（Q6）

- 快照是**衍生資料**：在匯入（或第一次回放）時，依世界模型推導，每隔固定事件數（例如每 1 萬筆，**估算**）或固定時間（例如 LOG 時間每 5 分鐘）存一份世界狀態。
- 取得時刻 t 的狀態 = 讀取 t 之前最近的快照 + 依 `seq` 套用其後的事件。
- 快照要記錄產生時使用的**廠別設定雜湊、Layout 版本、解析規則版本、程式版本**；任一改變就失效並重建（例如套用新版 Layout 後，ADR-0009 會留下稽核紀錄）。
- 大小估算：狀態以儲位為主（逐格記錄），數千至數萬格時，一份快照壓縮後約數十 KB 至 1 MB（**估算**）；每天 70 份以內約數 MB 至數十 MB。

### 3.7 案件資料夾結構（S2、D3）

建議（未決定）把「不可重建」與「可重建」的內容分開：

```text
（範例／虛構）
Cases\
  2026-01-05_範例廠_站點異常\         ← 一個案件
    請勿外流-本資料夾含真實LOG.txt     ← ADR-0009
    case.db                         ← 不可重建：案件中繼資料、廠別設定與 Layout 快照、原始檔清單與雜湊、偏移量、稽核紀錄
    raw\                            ← 不可重建：原始檔複本（永不修改；ADR-0002）
    index\                          ← 可重建：刪除後可由 raw\ + case.db 重新產生
      events.db                     ← 事件基本欄位、解析欄位、參照表、快照
      text-2026-01-04.db            ← 依日分片的正規化文字與全文索引
      text-2026-01-05.db
```

- 衍生資料可以重建，因此程式改版造成索引格式不相容時，只要「重建索引」，不必重新匯入（呼應 ADR-0001「新版要能讀舊版資料」：只要能讀 `case.db` 與 `raw\` 即可）。
- 整個資料夾複製即為備份；刪除案件即刪除整個資料夾。

---

## 4. 候選方案

### 4.1 候選方案 A：SQLite（Microsoft.Data.Sqlite）＋ FTS5 trigram

**在 .NET 10 的使用方式與原生相依**

- `Microsoft.Data.Sqlite`（MIT，Microsoft 官方；最新 10.0.x 相依 `SQLitePCLRaw.bundle_e_sqlite3` 2.1.13）（來源：NuGet 套件中繼資料）。
- 預設 bundle「在所有平台提供一致的 SQLite 版本，**內含 FTS4、FTS5、JSON1、R\*Tree**」（[Custom SQLite versions](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/custom-versions)）。
- **需要原生 DLL**：`SQLitePCLRaw.lib.e_sqlite3` 套件內含 `runtimes/win-x64/native/e_sqlite3.dll`（約 2 MB）（來源：3.53.3 版套件內容清單；Microsoft.Data.Sqlite 10.0.13 相依的 2.1.13 版的檔案配置**待打包時確認**）。self-contained 發佈時 SDK 會把它複製到發佈資料夾；ADR-0007 採用單一資料夾而非單一 exe，因此不會遇到單檔解壓到暫存資料夾的問題。**待實測**：在乾淨、離線的 Windows 10 上執行。
- 另一選項 `bundle_winsqlite3` 使用 Windows 內建的 `winsqlite3.dll`（來源同上），但 FTS5／trigram 是否可用、版本為何**待確認**，不建議。
- trigram 需要 SQLite 3.34.0 以上（[3.34.0 release log](https://www.sqlite.org/releaselog/3_34_0.html)：「Enhanced FTS5 to support trigram indexes」）。bundle 實際內含的 SQLite 版本以 `select sqlite_version()` 實測確認。

**授權**

- SQLite：公有領域，不需要授權（[SQLite Copyright](https://www.sqlite.org/copyright.html)）。
- SQLitePCLRaw：Apache-2.0（[SQLitePCL.raw](https://github.com/ericsink/SQLitePCL.raw)、NuGet 中繼資料）。
- Microsoft.Data.Sqlite：MIT（NuGet 中繼資料）。

**部分比對、不分大小寫、全半形**

- trigram tokenizer「支援一般的子字串比對，而不只是完整 token 比對」；支援以索引加速 `LIKE` 與 `GLOB`（來源：[FTS5 4.3.4 節](https://www.sqlite.org/fts5.html)）。
- 限制（來源，同上）：
  - **少於 3 個 Unicode 字元的子字串，在全文查詢中不會比對到任何列**。
  - `case_sensitive=1` 時只能加速 `GLOB`，不能加速 `LIKE`；`LIKE` 帶 `ESCAPE` 時無法使用索引。
  - `detail=none` 或 `detail=column` 時，全文查詢中的 token 不能超過 3 個字元，但 `LIKE`／`GLOB` 仍可使用（稍慢），可以縮小索引。
- 全半形與 Unicode 大小寫：FTS5 不做 NFKC（來源同上），所以存入 FTS 的是 3.1 節的**正規化文字**，tokenizer 設 `case_sensitive 1`（文字已摺疊，避免二次處理），查詢以 `GLOB '*關鍵字*'` 或以雙引號片語的 `MATCH` 執行（兩者的效能差異**待實測**）。
- 少於 3 字的詞：依 3.4 節的掃描路徑處理。
- 索引大小：官方唯一的數據是以一般 tokenizer 對約 1636 MiB 電子郵件建立的索引：`detail=full` 743 MiB、`detail=column` 340 MiB、`detail=none` 134 MiB（來源，同上）。**trigram 索引沒有官方數據**；因為每個字元位置都會產生一個 token，大小推估可能為正規化文字的 1～3 倍（**估算，待實測**）。

**正規表示式**

- SQLite 沒有內建 `regexp()`；可以用 `SqliteConnection.CreateFunction("regexp", ...)` 以 .NET `Regex` 實作，`X REGEXP Y` 會呼叫 `regexp(Y, X)`（[User-defined functions](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/user-defined-functions)、[SQLite 運算式文件](https://www.sqlite.org/lang_expr.html)）。
- 實際建議在 .NET 端逐分片讀出文字再比對（3.4 節），以便回報進度與取消。

**依時間排序與穩定排序**

- 以 `seq` 當 rowid（3.2 節），`ORDER BY rowid` 為主鍵順序讀取，不需額外排序；時間區間先換算成 `seq` 範圍。

**解析欄位與多值欄位**

- 一般欄位：事件表的欄位，或「事件—欄位」窄表；多值（桶）：參照表多列（3.5 節）。皆可建立 B-tree 索引。

**狀態快照**

- 快照表 (seq, 版本鍵, 壓縮後 BLOB)。SQLite 官方實驗顯示讀寫小型 BLOB 與個別檔案相當或更快（[35% Faster Than The Filesystem](https://www.sqlite.org/fasterthanfs.html)，作者自述數據為近似值）。

**案件資料夾可攜性**

- 每個資料庫是單一檔案；最大約 281 TB（[Limits](https://www.sqlite.org/limits.html)）。
- **注意 WAL**：WAL 模式下開啟中的資料庫會有 `-wal`、`-shm` 檔；WAL 檔是資料庫持久狀態的一部分，複製時若與資料庫分開，已提交的交易可能遺失或檔案損毀；最後一個連線正常關閉時會做 checkpoint 並刪除 WAL（[WAL 文件](https://www.sqlite.org/wal.html)）。
  - 建議（候選）：匯入期間使用 WAL 加速；關閉案件時確實關閉所有連線（或切回 `journal_mode=DELETE`），讓資料夾在程式關閉後只剩完整的 `.db` 檔；說明文件提醒「複製案件前先關閉程式或關閉案件」。

**寫入／匯入速度**

- 官方沒有適用於本案的匯入速度數據。估算：單一交易批次寫入事件表可達每秒數十萬列；FTS5 trigram 建索引較慢，推估每秒 5～30 MB 文字（**估算，待實測**）。7 天 3.5 GB 約 2～12 分鐘；依日分片可平行建立。

**對效能目標的初步估算（估算，待實測）**

| 查詢 | 估算 |
|---|---|
| 一天份、≥ 3 字關鍵字 | trigram 索引查詢通常在 1 秒內取得第一批結果；常見字組（例如英文 `SQL`、`INS`）的候選數量大，需實測 |
| 一天份、< 3 字關鍵字 | 掃描約 500 MB 正規化文字；SSD 上 0.5～2 秒（依磁碟與快取狀態） |
| 七天份 | 7 個分片平行；結果依時間串流，最早一天的結果會先出現，「開始顯示」通常早於全部完成 |

**風險**

| 風險 | 影響 | 緩解 |
|---|---|---|
| trigram 索引體積大 | 案件資料夾膨脹（7 天可能數 GB 至十數 GB，估算） | 實測 `detail=none`；只對正規化文字建索引；依日分片可以單獨刪除重建 |
| 2 字中文詞無法用索引 | 這類查詢退回掃描 | 掃描路徑必須達標；必要時加自建的 2-gram 輔助索引（見方案 F） |
| 自訂 FTS5 tokenizer 要用 C API | 無法在 C# 中簡單擴充 tokenizer | 不自訂 tokenizer，正規化在寫入前完成 |
| SQLitePCLRaw 由個人維護者維護 | 長期更新風險 | 授權允許自行建置；SQLite 本身極穩定；儲存層以介面隔離（C4） |

---

### 4.2 候選方案 B：Lucene.NET

**在 .NET 10 的使用方式與原生相依**

- 純 managed 程式碼，**不需要原生 DLL**。
- 現行版本 `4.8.0-beta00018`（2026-06），仍標示為 **beta**；官網稱其「極為穩定」、已有人用於正式環境，但未宣布正式版；支援 .NET Standard 2.0／2.1、.NET Framework 4.6.2 以上（[Lucene.NET 官網](https://lucenenet.apache.org/)、NuGet）。
- 需要 `Lucene.Net.Analysis.Common`（n-gram）、`Lucene.Net.Queries` 等子套件。

**授權**：Apache License 2.0（來源同上）。

**部分比對、不分大小寫、全半形**

- `NGramTokenizer` 把輸入切成指定長度的 n-gram，以 Unicode code point 計算、不切開代理對，且 offset 對應原文（[NGramTokenizer API](https://lucenenet.apache.org/docs/4.8.0-beta00017/api/analysis-common/Lucene.Net.Analysis.NGram.NGramTokenizer.html)）。
- 可以設定 1～3-gram 來支援 1、2 字查詢（trigram 的缺口在此可以補上），但索引會更大（**估算**：可能是文字的數倍）。
- 子字串查詢要用 n-gram 的片語查詢（需要位置資訊）。NFKC 與大小寫一樣要在寫入前（或自訂 CharFilter）處理。

**正規表示式**

- `RegexpQuery` 比對的是**索引中的 term**，且官方警告可能很慢（[RegexpQuery API](https://lucenenet.apache.org/docs/4.8.0-beta00017/api/core/Lucene.Net.Search.RegexpQuery.html)）。在 n-gram 索引上無法表達「訊息全文符合某正規表示式」，**仍需掃描**儲存的原文。

**依時間排序與穩定排序**

- 用數值 DocValues 欄位存 `seq`，以排序查詢取得結果；Lucene 文件編號會因 segment 合併而改變，**不能依賴文件編號當排序依據**，必須明確以 `seq` 排序（推論自 Lucene 索引格式：索引由多個獨立 segment 組成並會合併，[Lucene 4.8 索引格式](https://lucene.apache.org/core/4_8_0/core/org/apache/lucene/codecs/lucene46/package-summary.html)）。

**解析欄位與多值欄位**：同一欄位可以加入多個值（多值欄位），適合桶編號；以 `TermQuery` 篩選。

**狀態快照**：不適合放在 Lucene；需要另一個儲存（例如 SQLite 或檔案）。

**案件資料夾可攜性**：索引是一個**資料夾、多個檔案**（每個 segment 多個副檔名，可合併為 `.cfs`），有 `write.lock`（來源同上）。整個資料夾複製即可，但寫入中複製可能不一致。

**寫入／匯入速度**：Lucene 以批次建索引見長；n-gram 會大幅增加 token 數。沒有適用本案的官方數據（**待實測**）。

**效能估算**：≥ 3 字與 1～2 字查詢都可以走索引，搜尋端可能最快；但以 `seq` 排序需要取得全部符合文件後排序，或依日分片後合併（**估算**）。

**風險**：長期處於 beta；團隊需要學習 Lucene 的分析器與查詢模型；結構化欄位、快照仍需第二個儲存，形成兩套資料（一致性與重建流程更複雜）；2 GB 以上的 n-gram 索引在桌面電腦上的合併成本**待實測**。

---

### 4.3 候選方案 C：LiteDB

**在 .NET 10 的使用方式與原生相依**：純 managed，**不需要原生 DLL**；最新穩定版 5.0.21（2024-07），另有 6.0 預覽版（2026-10）（NuGet）。

**授權**：MIT（NuGet）。

**部分比對、不分大小寫、全半形**

- **沒有全文搜尋**（官方功能列表只列出欄位索引）（[LiteDB GitHub](https://github.com/mbdavid/LiteDB)）。
- 依運算式查詢屬於「full scan search」（[Expressions](https://www.litedb.org/docs/expressions/)）；`LIKE '%詞%'` 是否能用索引，官方文件**未說明**，推定為全掃描（**待確認**）。
- 資料檔的排序規則（collation）在建立時固定為**建立當下的 `CurrentCulture`＋IgnoreCase**，只能用 rebuild 變更（[Collation](https://www.litedb.org/docs/collation/)）。這表示比對行為與建立案件那台電腦的文化設定有關，對「案件可複製到其他電腦」是風險。

**正規表示式**：在 .NET 端掃描。

**依時間排序**：在 `seq` 欄位建索引即可。

**解析欄位與多值欄位**：**優點**：陣列欄位可以建立多鍵索引（multikey），陣列中每個值都會進索引（[Indexes](https://www.litedb.org/docs/indexes/)），適合桶編號。

**狀態快照**：可存成文件（單一 BSON 文件上限 16 MB）（[LiteDB Docs](https://www.litedb.org/docs/)）。

**案件資料夾可攜性**：單一資料檔（官方描述 "single data file"）；v5 另有交易記錄檔（官方 README 提到 WAL 式復原），複製時的注意事項**待確認**。

**寫入／匯入速度**：BSON 文件序列化，大量（數百萬筆）寫入的速度**待實測**；推估低於 SQLite 的批次插入（**估算**）。

**效能估算**：關鍵字搜尋需要對數 GB 文件逐筆反序列化後比對，**估算難以達到七天 10 秒**。

**風險**：無全文搜尋；穩定版最近一次發布在 2024 年；collation 綁定文化設定。**不建議**作為主要的搜尋儲存，可以作為「結構化欄位＋快照」的替代選項，但相較 SQLite 沒有明顯優勢。

---

### 4.4 候選方案 D：DuckDB（DuckDB.NET）

**在 .NET 10 的使用方式與原生相依**

- `DuckDB.NET.Data.Full`（MIT；1.5.6.1，以 DuckDB v1.5.6 建置，2026-10-15；含 net8.0 與 net10.0 組件）（NuGet）。「Full」套件內含 DuckDB 原生程式庫（[DuckDB.NET 入門](https://duckdb.net/docs/getting-started.html)）。
- **需要原生 DLL**：`runtimes/win-x64/native/duckdb.dll` 約 **37 MB**（來源：套件內容清單）。
- DuckDB 本身授權為 MIT（套件內附 `LICENSE-DuckDB.txt`）。

**部分比對、不分大小寫、全半形**

- 有 `contains`、`ilike_escape`、`lower`、`strip_accents`、`nfc_normalize`；**沒有 NFKC 函式**（[Text functions](https://duckdb.org/docs/current/sql/functions/text.html)）。仍需由 .NET 產生正規化文字欄位。
- 子字串查詢以欄式掃描執行；DuckDB 的 zonemap（每個 row group 的最小／最大值）可以依排序後的欄位跳過資料（[Indexing](https://duckdb.org/docs/current/guides/performance/indexing.html)），對 `seq`、時間區間很有效，但對子字串**沒有索引**。
- FTS 擴充：以 BM25 為主，預設 Porter stemmer 與英文停用詞，**資料變動後索引不會自動更新**；預設從官方擴充庫**自動下載**載入（[Full-Text Search Extension](https://duckdb.org/docs/current/core_extensions/full_text_search.html)）。離線環境需要預先放置擴充檔（[Installing Extensions](https://duckdb.org/docs/current/extensions/installing_extensions.html) 提到可設定 `extension_directories` 供 air-gapped 環境使用），且以詞為單位，不符合 ADR-0010 的部分比對，**不建議使用 FTS 擴充**。

**正規表示式**：內建，使用 **RE2** 引擎（[Regular expressions](https://duckdb.org/docs/current/sql/functions/regular_expressions.html)），語法與 .NET 不同；若讓使用者輸入的正規表示式在兩處執行會不一致，因此仍建議在 .NET 端執行（3.4 節）。

**依時間排序**：`ORDER BY seq`；資料依 `seq` 寫入時 zonemap 效果最好（來源同上）。

**解析欄位與多值欄位**：支援 LIST 型別存多值；但點查（以命令編號找事件）沒有 B-tree 型的次要索引效益（ART 索引只對等值與 `IN` 有幫助，且會拖慢寫入）（來源同上）。

**狀態快照**：可存 BLOB，但 DuckDB 為分析型資料庫，不是為小筆隨機讀寫設計（**推論**）。

**案件資料夾可攜性**：單一資料庫檔；v0.10 起保證新版可讀舊版檔，向前相容只盡力而為（[Storage](https://duckdb.org/docs/current/internals/storage.html)）；同一時間只能有一個行程讀寫（[Concurrency](https://duckdb.org/docs/current/connect/concurrency.html)）。

**寫入／匯入速度**：欄式批次寫入（Appender）一般很快（**估算，待實測**）。

**效能估算**：多執行緒向量化掃描，對 3.5 GB 文字做 `contains` 掃描可能在數秒內完成（**估算，待實測**）；不受 3 字下限限制。

**風險**：原生 DLL 大（37 MB）；子字串無索引，完全依賴掃描速度；點查與小筆讀寫非強項；FTS 擴充需要網路下載。適合作為「掃描路徑」或分析用途的**比較對象**，不建議作為唯一儲存。

---

### 4.5 候選方案 E：自訂檔案格式＋自建索引

**做法（候選）**：每天一個檔案，依 `seq` 順序存放正規化文字（例如 UTF-8），另存「事件位移表」（第 n 筆事件在檔案中的起點，用來從比對位置換回事件）與結構化欄位的排序陣列；搜尋時以 `MemoryMappedFile` 或串流讀取，用 .NET 向量化的 `IndexOf`／`SearchValues` 掃描。

- **原生相依**：無；**授權**：自有程式碼。
- **部分比對**：掃描天生支援任意長度（含 1、2 字）、AND／OR／NOT、正規表示式，規則完全由程式控制。
- **排序**：檔案本身依 `seq` 排列，掃描結果天然依時間順序，可以邊掃邊顯示。
- **解析欄位、多值、串連、快照**：都要自己設計檔案格式與索引——這是本方案最大的成本。
- **可攜性**：多個檔案；格式版本與損毀偵測要自己處理。
- **匯入速度**：最快（只寫順序檔）（**估算**）。
- **效能估算（估算，待實測）**：
  - 掃描速度上限主要是磁碟：一般硬體的循序讀取約 SATA SSD 0.5 GB/s、NVMe 1.5～3.5 GB/s、傳統硬碟 0.1～0.2 GB/s（**一般硬體規格的假設值**）。
  - 一天 500 MB：SSD 約 0.2～1 秒，可達 2 秒目標；七天 3.5 GB：NVMe 約 1～3 秒、SATA SSD 約 7 秒，可達 10 秒目標；**傳統硬碟無法達標**。
  - 檔案已在作業系統快取中時，速度由記憶體與 CPU 決定，會快很多。
  - 目標是「**開始顯示結果**」：依時間順序掃描時，第一筆符合通常很快出現；但時間軸總覽的「所有符合位置」與筆數要等掃描完成。
- **風險**：自建格式的正確性、損毀復原、版本相容全部自行負責；工程師電腦若是傳統硬碟或磁碟很慢，七天份無法達標。

---

### 4.6 其他考慮過的方案（未深入）

| 方案 | 未深入的原因 |
|---|---|
| Elasticsearch、OpenSearch、Seq、Grafana Loki 等伺服器型產品 | 需要另外架設服務，違反 ADR-0001／ADR-0007 |
| Tantivy（Rust 全文搜尋程式庫） | 沒有官方 .NET 綁定；需要自行處理原生互通 |
| ESENT（Windows 內建資料庫引擎，透過 ManagedEsent 使用） | 不需要額外原生 DLL 是優點；但沒有全文搜尋、API 層級低，相較 SQLite 沒有明顯優勢（**未以一手來源細查**） |
| SQLite ICU 擴充（Unicode 大小寫的 `LIKE`） | 預設 bundle 不含 ICU；需要自行建置原生程式庫；且仍沒有 NFKC |

### 4.7 候選方案 F（混合）：SQLite ＋ 正規化文字掃描 ＋ 選用 n-gram 加速

這是第 1 節建議的完整形態：

| 層 | 內容 | 技術（候選） |
|---|---|---|
| 案件中繼資料 | 案件、原始檔清單與雜湊、廠別設定快照、偏移量、稽核 | SQLite `case.db` |
| 事件與欄位 | 基本欄位、解析欄位、參照表（命令、棧板、桶、別名、儲位）、快照 | SQLite `events.db`，rowid＝`seq` |
| 搜尋用文字 | 依日分片的正規化文字 | SQLite 分片檔中的欄位，或自訂順序檔（方案 E 的做法） |
| 子字串加速 | ≥ 3 字：FTS5 trigram；1～2 字：掃描（必要時以自建 2-gram 輔助索引補足） | FTS5 trigram（`detail` 選項待實測） |
| 正規表示式 | 依日、依時間順序掃描原文，回報進度、可取消 | .NET `Regex` |

「搜尋用文字」放在 SQLite 還是自訂順序檔，是本方案內唯一需要用實測決定的分歧：放 SQLite 較單純（一種檔案、交易一致）；自訂順序檔掃描最快、最可預期。

---

## 5. 比較表

符號：◎ 很適合／○ 可以／△ 需額外工作或有疑慮／✕ 不適合。評等為依第 4 節來源與**估算**的初步判斷，**需實測修正**。

| 面向 | A SQLite＋FTS5 trigram | B Lucene.NET | C LiteDB | D DuckDB | E 自訂檔案＋索引 | F 混合（建議） |
|---|---|---|---|---|---|---|
| .NET 10 使用 | ◎ Microsoft 官方套件 | ○ 社群移植，4.8 仍為 beta | ○ | ○ | ◎ 自行撰寫 | ◎ |
| 原生 DLL | 需要 `e_sqlite3.dll`（約 2 MB） | 不需要 | 不需要 | 需要 `duckdb.dll`（約 37 MB） | 不需要 | 需要 `e_sqlite3.dll` |
| 授權 | 公有領域＋Apache-2.0＋MIT | Apache-2.0 | MIT | MIT | 自有 | 同 A |
| 部分比對 ≥ 3 字 | ◎ 索引 | ◎ 索引 | ✕ 全掃描 | ○ 掃描 | ○ 掃描 | ◎ 索引 |
| 部分比對 1～2 字 | △ 掃描 | ◎（1～3-gram，索引更大） | ✕ | ○ 掃描 | ○ 掃描 | ○ 掃描／可加 2-gram |
| 不分大小寫／NFKC | △ 自行正規化 | △ 自行正規化 | △ 綁定建立時的文化設定 | △ 自行正規化（只有 NFC） | △ 自行正規化 | △ 自行正規化 |
| 正規表示式 | ○ .NET 掃描 | △ 只能比對 term，需掃描 | ○ .NET 掃描 | ○ RE2（語法與 .NET 不同） | ◎ .NET 掃描 | ◎ .NET 掃描 |
| 依時間穩定排序 | ◎ rowid＝`seq` | ○ 需明確排序 | ○ | ○ | ◎ 檔案順序 | ◎ |
| 解析欄位／多值 | ◎ 參照表 | ◎ 多值欄位 | ◎ 多鍵索引 | ○ LIST，點查弱 | △ 自建 | ◎ |
| 命令／棧板串連 | ◎ B-tree | ○ | ○ | △ | △ 自建 | ◎ |
| 狀態快照 | ◎ BLOB 表 | ✕ 需另存 | ○ | △ | △ 自建 | ◎ |
| 案件可攜性 | ○ 單檔（注意 WAL） | ○ 多檔資料夾 | ○ 單檔（注意記錄檔） | ○ 單檔 | △ 多檔、自訂格式 | ○ 數個單檔 |
| 匯入速度 | ○（trigram 較慢，待實測） | ○（待實測） | △ | ◎ | ◎ | ○ |
| 一天 2 秒（估算） | 可能達成 | 可能達成 | 不易 | 可能達成 | SSD 可達成 | 可能達成 |
| 七天 10 秒（估算） | 可能達成（需分片並行） | 可能達成 | 不易 | 可能達成 | NVMe／SATA SSD 可達成，硬碟不行 | 可能達成 |
| 維護風險 | 低 | 中（beta、學習成本） | 中（更新頻率） | 中（大型原生依賴） | 高（自建格式） | 中低 |

---

## 6. 建議（未決定）

> 以下為**建議（未決定）**，所有技術皆為**候選方案**，須經使用者確認後另立 ADR。

1. **主要儲存**：SQLite（`Microsoft.Data.Sqlite` ＋ 預設 `e_sqlite3` bundle）。理由：授權最寬鬆、Microsoft 官方維護 .NET 存取層、內含 FTS5、單檔案、適合結構化欄位、多值參照表與快照；儲存層以介面隔離（C4）。
2. **全文搜尋**：方案 F——寫入前由 .NET 產生 NFKC＋大小寫摺疊的正規化文字；≥ 3 字的詞用 FTS5 trigram；1～2 字的詞、NOT-only 與正規表示式用依日平行、依時間順序的掃描。
3. **事件序號**：匯入後依 ADR-0004 規則編 `seq` 並當作 rowid；穩定識別碼另存。
4. **分片**：可重建的全文資料依日分檔；不可重建的資料（`case.db`、`raw\`）與可重建的 `index\` 分開。
5. **先做的實測**（第 8 節）決定兩件事：(a) 正規化文字放 SQLite 還是自訂順序檔；(b) trigram 索引的 `detail` 選項與實際大小是否可接受。如果實測顯示**單靠掃描**就能在目標硬體上達標，可以考慮**不建 trigram 索引**（省下大量磁碟與匯入時間），只保留掃描路徑。
6. **不建議**：LiteDB（無全文搜尋、collation 綁定文化設定）；DuckDB 與 Lucene.NET 保留為實測時的比較對象，不作為第一選擇（DuckDB：原生 DLL 大、子字串無索引；Lucene.NET：長期 beta，且結構化資料與快照仍需第二套儲存）。

---

## 7. 案件資料夾的預設位置

**建議（未決定）**：`%LOCALAPPDATA%\<程式名稱>\Cases\`（預設路徑 `C:\Users\<帳號>\AppData\Local\<程式名稱>\Cases\`），並允許使用者在設定中改到其他位置。

| 候選位置 | 優點 | 缺點 |
|---|---|---|
| **`%LOCALAPPDATA%`**（建議） | 屬於使用者個人（PERUSER），其他帳號預設看不到（ADR-0009）；不在程式資料夾內（ADR-0007）；「Local」不隨漫遊設定檔同步；**不在 OneDrive 已知資料夾移動的範圍內** | 預設是隱藏資料夾，使用者不易找到 → 程式提供「在檔案總管開啟案件資料夾」；公司的備份機制可能不包含 AppData（**待向 IT 確認**） |
| 「文件」（`%USERPROFILE%\Documents`） | 使用者容易找到、複製 | 企業可用群組原則**靜默**把「桌面、文件、圖片」等已知資料夾移到 OneDrive（[Redirect known folders to OneDrive](https://learn.microsoft.com/en-us/sharepoint/redirect-known-folders)）；真實 LOG 可能因此被同步到雲端，與「真實 LOG 不提交到未核准的外部服務」（CLAUDE.md 第 13 條）衝突；大量小檔與數 GB 索引的同步也會拖慢電腦 |
| `%APPDATA%`（Roaming） | 個人資料夾 | 網域環境可能漫遊同步，不適合 GB 級資料 |
| 程式資料夾 | 免安裝最直覺 | ADR-0007 明確排除（更新時整個資料夾替換） |

- `FOLDERID_Documents` 的預設路徑是 `%USERPROFILE%\Documents`，屬於 PERUSER 類型（[KNOWNFOLDERID](https://learn.microsoft.com/en-us/windows/win32/shell/knownfolderid)）。
- 補充建議：程式偵測到使用者把案件位置設在 OneDrive 同步資料夾內時顯示警告（**候選功能**）。
- 讀取 `%LOCALAPPDATA%` 時用 `Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)`，不要寫死路徑。

---

## 8. 需要實測驗證的項目

實測只能使用**虛構產生的測試資料**或在本機以樣本進行（ADR-0009），結果不得把真實 LOG 內容寫入 repo。

| 編號 | 項目 | 量測方式 | 判定 |
|---|---|---|---|
| T-01 | self-contained 發佈含 `e_sqlite3.dll` 能在乾淨、離線的 Windows 10／11 執行；`sqlite_version()` ≥ 3.34.0；FTS5 trigram 可用 | 依 ADR-0007 技術驗證流程 | 必須通過 |
| T-02 | 產生虛構資料：一天 500 MB（約 70 萬筆，含 Big5／UTF-8、延續行、同時間戳記），共 7 天 | 測試資料產生器 | — |
| T-03 | 匯入時間：解碼＋正規化＋寫入事件表＋參照表＋trigram 索引 | 一天、七天；單執行緒與依日平行 | 提供給 P3 訂目標 |
| T-04 | 索引大小：trigram `detail=full`／`column`／`none` | 與正規化文字大小相比 | 使用者判斷可接受上限 |
| T-05 | ≥ 3 字關鍵字（罕見詞、常見詞）在一天／七天的「第一筆結果時間」與「全部完成時間」 | 冷快取（重新開機後）與熱快取 | P1 |
| T-06 | 1～2 字中文詞的掃描時間（SQLite 欄位掃描 vs 自訂順序檔掃描 vs DuckDB `contains`） | 同上 | P1；決定 4.7 的分歧 |
| T-07 | AND／OR／NOT 組合＋篩選條件（模組、等級、命令編號）的查詢時間 | 同上 | P1 |
| T-08 | 正規表示式掃描一天／七天的時間，取消的反應時間 | 一般引擎與 NonBacktracking | 無目標，記錄供參考 |
| T-09 | 以命令編號、棧板編號串連事件的查詢時間 | 七天份 | 建議 < 0.5 秒（**假設**） |
| T-10 | 快照：產生時間、大小、從快照推導任一時刻的時間 | 七天份；每 N 筆一份 | 回放拖曳時間軸的體感 |
| T-11 | 穩定排序：同一查詢重複執行順序一致；與時間軸順序一致 | AC-004 | 必須通過 |
| T-12 | 可攜性：關閉程式後複製案件資料夾到另一台電腦開啟，結果一致；複製時不留下 `-wal` | 兩台電腦 | 必須通過 |
| T-13 | NFKC／大小寫：全形英數、半形片假名、全形符號等虛構案例的比對結果一致；不同 Windows 版本的結果一致 | 測試表 | 必須通過 |
| T-14 | 目標硬體：工程師電腦的磁碟類型（NVMe／SATA SSD／硬碟）與記憶體 | 向使用者確認 | 若有硬碟，P1 需重新評估 |

---

## 9. 與 CLAUDE.md 3.4 設計面向的對照

| 面向 | 本研究的處理 |
|---|---|
| 時間戳記 | 沿用 ADR-0003（100 μs、保留原始字串、時間無效沿用前一筆）；儲存層以 64 位元整數存標準化時間（100 μs 刻度，候選），原始字串另存 |
| 時區 | 沿用 ADR-0003（廠別時區，預設 UTC+8，不處理日光節約）；儲存的是廠別當地時間，時區記在 `case.db` |
| 時鐘偏差 | ADR-0003 不做模組間校正；案件偏移量只影響顯示與查詢的換算，不重建索引、不改 `seq` |
| 事件排序 | `seq` 依 ADR-0004 規則編號，所有查詢依 `seq` 輸出；同時間戳記的先後只為穩定，不代表因果 |
| 原始 LOG 可追溯性 | 每筆事件存穩定識別碼與位元組位移；原始檔在 `raw\` 永不修改；索引可刪除重建 |
| 資料完整性 | 匯入以交易為單位（失敗不留半套資料）；衍生資料記錄來源檔雜湊、解析規則與程式版本；輪替檔與重新匯入依 ADR-0002／ADR-0004 處理，`seq` 可重編而識別碼不變 |

---

## 10. 待確認問題

| 編號（暫） | 問題 | 對設計的影響 |
|---|---|---|
| R-Q1 | 工程師電腦的磁碟類型與記憶體大小 | 掃描路徑是否能達成七天 10 秒；是否必須建 trigram 索引 |
| R-Q2 | 案件資料夾可接受的最大磁碟用量 | trigram 索引的取捨（T-04） |
| R-Q3 | 公司備份機制是否涵蓋 `%LOCALAPPDATA%` | 預設位置的選擇；若不涵蓋，需提醒使用者自行複製 |
| R-Q4 | 匯入時間的可接受上限 | 是否在匯入時就建立全部索引，或延後背景建立 |
| R-Q5 | 是否需要「區分大小寫」以外的精確比對（例如區分全半形） | 是否要保存第二份正規化文字 |

---

## 11. 來源清單

- SQLite：[FTS5](https://www.sqlite.org/fts5.html)、[3.34.0 release log](https://www.sqlite.org/releaselog/3_34_0.html)、[SQL 運算式（LIKE、REGEXP）](https://www.sqlite.org/lang_expr.html)、[WAL](https://www.sqlite.org/wal.html)、[Limits](https://www.sqlite.org/limits.html)、[Copyright](https://www.sqlite.org/copyright.html)、[35% Faster Than The Filesystem](https://www.sqlite.org/fasterthanfs.html)
- Microsoft.Data.Sqlite：[Custom SQLite versions](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/custom-versions)、[User-defined functions](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/user-defined-functions)
- SQLitePCLRaw：[GitHub ericsink/SQLitePCL.raw](https://github.com/ericsink/SQLitePCL.raw)；NuGet 套件中繼資料與內容清單（`Microsoft.Data.Sqlite` 10.0.13、`SQLitePCLRaw.bundle_e_sqlite3` 2.1.13、`SQLitePCLRaw.lib.e_sqlite3` 3.53.3）
- .NET：[NormalizationForm](https://learn.microsoft.com/en-us/dotnet/api/system.text.normalizationform)、[Globalization and ICU](https://learn.microsoft.com/en-us/dotnet/core/extensions/globalization-icu)、[Globalization invariant mode](https://github.com/dotnet/runtime/blob/main/docs/design/features/globalization-invariant-mode.md)、[Regular expression options](https://learn.microsoft.com/en-us/dotnet/standard/base-types/regular-expression-options)
- Lucene.NET：[官網](https://lucenenet.apache.org/)、[NGramTokenizer](https://lucenenet.apache.org/docs/4.8.0-beta00017/api/analysis-common/Lucene.Net.Analysis.NGram.NGramTokenizer.html)、[RegexpQuery](https://lucenenet.apache.org/docs/4.8.0-beta00017/api/core/Lucene.Net.Search.RegexpQuery.html)、[Lucene 4.8 索引格式](https://lucene.apache.org/core/4_8_0/core/org/apache/lucene/codecs/lucene46/package-summary.html)；NuGet `Lucene.Net` 4.8.0-beta00018
- LiteDB：[Docs](https://www.litedb.org/docs/)、[Indexes](https://www.litedb.org/docs/indexes/)、[Collation](https://www.litedb.org/docs/collation/)、[Expressions](https://www.litedb.org/docs/expressions/)、[GitHub](https://github.com/mbdavid/LiteDB)；NuGet `LiteDB` 5.0.21
- DuckDB：[Full-Text Search](https://duckdb.org/docs/current/core_extensions/full_text_search.html)、[Installing Extensions](https://duckdb.org/docs/current/extensions/installing_extensions.html)、[Regular expressions](https://duckdb.org/docs/current/sql/functions/regular_expressions.html)、[Text functions](https://duckdb.org/docs/current/sql/functions/text.html)、[Storage](https://duckdb.org/docs/current/internals/storage.html)、[Concurrency](https://duckdb.org/docs/current/connect/concurrency.html)、[Indexing](https://duckdb.org/docs/current/guides/performance/indexing.html)、[DuckDB.NET](https://duckdb.net/docs/getting-started.html)；NuGet `DuckDB.NET.Data.Full`／`DuckDB.NET.Bindings.Full` 1.5.6.1
- Windows：[KNOWNFOLDERID](https://learn.microsoft.com/en-us/windows/win32/shell/knownfolderid)、[Redirect known folders to OneDrive](https://learn.microsoft.com/en-us/sharepoint/redirect-known-folders)

---

## 12. 變更紀錄

| 日期 | 版本 | 變更內容 |
|---|---|---|
| 2026-10-16 | v0.1 | 初版：五個候選方案與混合方案的比較、建議（未決定）、預設位置建議、實測清單 |
