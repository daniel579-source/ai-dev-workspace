using LogTrace.Engine.Import;
using LogTrace.Engine.Storage;
using Microsoft.Data.Sqlite;

namespace LogTrace.Engine.Cases;

/// <summary>
/// 案件資料庫的結構版本與程式不同時，以案件內保留的原始 LOG 重建（ADR-0030 第 6 點）：
/// <list type="number">
/// <item>舊資料庫備份到集中的備份資料夾（<c>&lt;存放位置的上一層&gt;\Backups\&lt;案件資料夾名稱&gt;\</c>，預設 D:\LogTrace\Backups），每個案件只保留最近一份。</item>
/// <item>在暫存檔建立新的資料庫，保留案件名稱、時間偏移量、廠別設定快照、匯入紀錄、檔案紀錄、稽核紀錄；
///   以案件記錄的廠別設定（建立時的快照，或之後套用的新版）重新解析原始 LOG。不累積到學習樣板（ADR-0024）。</item>
/// <item>成功後才取代 case.db。失敗或取消時刪除暫存檔，case.db 完全不動，案件仍是「需要重建」，下次開啟時再提示
///   （2026-11-27 使用者選 2a）。</item>
/// </list>
/// </summary>
public static class CaseUpgrade
{
    public const string BackupsFolderName = "Backups";
    const string TempSuffix = ".rebuilding";

    /// <summary>案件資料庫的結構版本與程式不同（需要重建）。讀不了結構版本的資料庫不算。</summary>
    public static bool NeedsRebuild(string caseFolder) =>
        SqliteEventStore.ReadSchemaVersion(Path.Combine(caseFolder, Case.DatabaseFileName)) is int v && v != SqliteEventStore.SchemaVersion;

    /// <summary>案件存放位置對應的備份資料夾：存放位置的上一層下的 Backups（D:\LogTrace\Cases → D:\LogTrace\Backups）。</summary>
    public static string BackupsRoot(string casesRoot)
    {
        var full = Path.GetFullPath(casesRoot).TrimEnd(Path.DirectorySeparatorChar);
        return Path.Combine(Path.GetDirectoryName(full) ?? full, BackupsFolderName);
    }

    /// <summary>一個案件的備份資料夾。</summary>
    public static string BackupFolder(string backupsRoot, string caseFolder) =>
        Path.Combine(backupsRoot, Path.GetFileName(Path.GetFullPath(caseFolder).TrimEnd(Path.DirectorySeparatorChar)));

    /// <summary>備份並重建；傳回備份的資料夾。失敗時丟出例外，case.db 不變。</summary>
    public static string Rebuild(string caseFolder, string backupsRoot, CancellationToken ct = default)
    {
        var db = Path.Combine(caseFolder, Case.DatabaseFileName);
        if (!File.Exists(db)) throw new FileNotFoundException("不是案件資料夾", db);
        var oldVersion = SqliteEventStore.ReadSchemaVersion(db);

        // 1. 把 WAL 寫回 case.db，再備份（只保留最近一份）
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
        }
        var backup = BackupFolder(backupsRoot, caseFolder);
        if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
        Directory.CreateDirectory(backup);
        File.Copy(db, Path.Combine(backup, Case.DatabaseFileName));
        File.WriteAllText(Path.Combine(backup, "說明.txt"),
            $"""
            LogTrace 案件資料庫的備份（ADR-0030 第 6 點）。
            備份時間：{DateTime.Now:yyyy-MM-dd HH:mm:ss}
            案件資料夾：{caseFolder}
            原本的結構版本：{oldVersion?.ToString() ?? "（不明）"}；重建後：{SqliteEventStore.SchemaVersion}

            要以舊版程式開啟這個案件時：先關閉 LogTrace，把這裡的 case.db 複製回案件資料夾，取代那裡的 case.db。
            這個資料夾只保留最近一次重建前的資料庫。
            """.ReplaceLineEndings("\r\n"), System.Text.Encoding.UTF8);
        ct.ThrowIfCancellationRequested();

        // 2. 在暫存檔建立新的資料庫
        var temp = db + TempSuffix;
        DeleteDatabase(temp);
        try
        {
            using (var store = new SqliteEventStore(temp))
            {
                store.CopyPreservedFrom(db);
                var info = store.LoadInfo();
                var (files, events, fields, effects) = CaseImporter.Rebuild(caseFolder, info.Plant, info.Correction, store, ct);
                ct.ThrowIfCancellationRequested();
                store.Rebuild(files, events, fields, effects, info, null, null,
                    AuditEntry.Now("重建案件資料庫", info.Name,
                        $"結構版本 {oldVersion?.ToString() ?? "不明"} → {SqliteEventStore.SchemaVersion}；事件 {events.Count} 筆；備份：{backup}"));
                store.Checkpoint();
            }
            // 3. 成功後才取代
            DeleteDatabase(db, keepMain: true);
            File.Move(temp, db, overwrite: true);
        }
        catch
        {
            DeleteDatabase(temp);
            throw;
        }
        return backup;
    }

    static void DeleteDatabase(string path, bool keepMain = false)
    {
        foreach (var f in keepMain ? new[] { path + "-wal", path + "-shm" } : [path, path + "-wal", path + "-shm"])
            if (File.Exists(f)) File.Delete(f);
    }
}
