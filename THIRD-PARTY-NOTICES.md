# 第三方元件與授權清單

LogTrace 發佈資料夾中包含下列第三方元件。授權全文在 `licenses\` 資料夾；所有元件都可以免付費使用（ADR-0007 第 3 點）。

| 元件 | 版本 | 用途 | 授權 | 授權全文 |
|---|---|---|---|---|
| .NET Runtime（含 Microsoft.NETCore.App） | 10.0.13 | 執行環境（self-contained 一起發佈） | MIT；內含元件見其第三方聲明 | `licenses\dotnet-runtime-LICENSE.txt`、`licenses\dotnet-runtime-THIRD-PARTY-NOTICES.txt` |
| Windows Desktop Runtime（WPF） | 10.0.13 | 畫面 | MIT | `licenses\dotnet-windowsdesktop-LICENSE.txt` |
| Dirkster.AvalonDock、Dirkster.AvalonDock.Core | 5.0.1 | Docking 多視窗 | Ms-PL | `licenses\AvalonDock-LICENSE.txt` |
| Dirkster.AvalonDock.Serializer.Xml | 5.0.1 | 版面儲存與還原 | Ms-PL | `licenses\AvalonDock-LICENSE.txt` |
| CommunityToolkit.Mvvm | 8.4.2 | ViewModel | MIT | `licenses\CommunityToolkit.Mvvm-LICENSE.txt`、`licenses\CommunityToolkit.Mvvm-ThirdPartyNotices.txt` |
| Microsoft.Data.Sqlite、Microsoft.Data.Sqlite.Core | 10.0.13 | 案件資料庫的存取 | MIT | `licenses\Microsoft.Data.Sqlite-LICENSE.txt` |
| SQLitePCLRaw（bundle_e_sqlite3、core、lib.e_sqlite3、provider.e_sqlite3） | 2.1.13 | SQLite 原生程式庫的載入 | Apache-2.0 | `licenses\Apache-2.0.txt` |
| SQLite（e_sqlite3.dll，隨 SQLitePCLRaw 提供） | — | 資料庫引擎 | 公有領域（Public Domain），見 <https://sqlite.org/copyright.html> | — |
| Squid-Box.SevenZipSharp.Lite | 1.6.2.24 | 呼叫 7-Zip 解開壓縮檔 | LGPL-3.0-only | `licenses\SevenZipSharp-LGPL-3.0.txt`（LGPL-3.0 以 GPL-3.0 為基礎：`licenses\GPL-3.0.txt`） |
| 7-Zip（`7z\7za.dll`，來自 7z.Libs 26.4.0） | 26.04 | 解開 7z／zip | GNU LGPL（部分程式碼為 BSD 3-clause、BSD 2-clause） | `licenses\7-Zip-7za-License.txt`（另有一份在 `7z\License.txt`） |

## 說明

- **LGPL 元件**（SevenZipSharp、7-Zip）以獨立的 DLL 動態載入，沒有修改；使用者可以自行替換為相容的版本。原始碼：SevenZipSharp <https://github.com/squid-box/SevenZipSharp>、7-Zip <https://www.7-zip.org/>。
- 只發佈 7-Zip 的 `7za.dll`，不包含有 unRAR 授權限制的 `7z.dll`。
- 測試用的元件（xUnit、FlaUI 等）不在發佈資料夾中，不列入本清單。
- 本清單由 `dotnet list package --include-transitive` 的結果與各套件的授權宣告整理（2026-10-16，#29）。新增或升級套件時要同步更新。
