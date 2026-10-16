using System.Globalization;
using Microsoft.Data.Sqlite;

namespace LogTrace.Engine.Storage;

/// <summary>全文索引的建立進度。</summary>
/// <param name="Day">正在處理的日期資料夾；完成時為 null。</param>
public sealed record IndexBuildProgress(int BuiltDays, int TotalDays, string? Day);

/// <summary>全文索引建立的結果。</summary>
/// <param name="LimitReached">案件磁碟用量會超過原始 LOG 的 5 倍，其餘日期不建索引（以掃描搜尋，ADR-0011 第 4 點）。</param>
public sealed record IndexBuildResult(int BuiltDays, int TotalDays, bool LimitReached, TimeSpan Elapsed);

/// <summary>
/// 全文索引（ADR-0011 第 1、2 點）：案件資料夾下的 index\，每個日期資料夾一個分片 &lt;日期&gt;.db，
/// 內容是搜尋用文字的 FTS5 trigram 索引（contentless、detail=none：只記錄哪些事件含有某個 3 字片段）。
/// 屬於可重建的資料，可以整個刪除再建立。
/// <list type="bullet">
/// <item>每個分片記錄建立時案件內容的版本；匯入、套用新版設定、修改偏移量後版本改變，舊分片視為未建立。</item>
/// <item>索引只用來找出「候選」事件，候選一律再以搜尋用文字確認，所以結果與掃描完全相同。</item>
/// </list>
/// </summary>
public sealed class SearchIndex(string caseFolder, string caseDbPath)
{
    public const string FolderName = "index";

    /// <summary>
    /// 案件磁碟用量的上限：原始 LOG 的倍數（ADR-0011 第 4 點：5 倍）。
    /// 測試資料很小時，SQLite 的固定開銷就超過 5 倍，測試會調高這個值。
    /// </summary>
    public double DiskLimitRatio { get; set; } = DefaultDiskLimitRatio;

    public const double DefaultDiskLimitRatio = 5;

    /// <summary>估計一天的索引大小：該日原始 LOG 大小的倍數（技術驗證 detail=none 約 0.23 倍，取寬鬆值）。</summary>
    const double EstimatedIndexRatio = 0.3;

    const string FormatVersion = "1";

    public string Folder => Path.Combine(caseFolder, FolderName);

    string ShardPath(string day) => Path.Combine(Folder, day + ".db");

    static SqliteConnection Open(string path, bool readOnly)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false, // 不留下連線，分片可以刪除與取代
        }.ToString());
        conn.Open();
        return conn;
    }

    /// <summary>某一天的分片是否已依目前的案件內容建好。</summary>
    public bool IsBuilt(string day, string contentVersion)
    {
        var path = ShardPath(day);
        if (!File.Exists(path)) return false;
        try
        {
            using var conn = Open(path, readOnly: true);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key = 'content_version'";
            return cmd.ExecuteScalar() as string == contentVersion;
        }
        catch (SqliteException)
        {
            return false; // 損壞或建立中斷：視為未建立
        }
    }

    /// <summary>
    /// 一個關鍵字（已正規化）在某一天的候選事件數的估計：組成它的 3 字片段中，出現在最少事件中的那個的事件數。
    /// 不能用索引的關鍵字（少於 3 字、含 surrogate）傳回 null。
    /// </summary>
    public long? Estimate(string day, string term)
    {
        var grams = Trigrams(term);
        if (grams is null) return null;
        using var conn = Open(ShardPath(day), readOnly: true);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT coalesce((SELECT doc FROM vocab WHERE term = $t), 0)";
        var p = cmd.Parameters.Add(new SqliteParameter { ParameterName = "$t" });
        long min = long.MaxValue;
        foreach (var g in grams)
        {
            p.Value = g;
            min = Math.Min(min, (long)cmd.ExecuteScalar()!);
            if (min == 0) break;
        }
        return min;
    }

    /// <summary>
    /// 含有所有片段的事件序號（候選）：terms 為「任一」關係，每個關鍵字取它的 3 字片段的交集。依序號排序。
    /// </summary>
    public List<long> Candidates(string day, IReadOnlyList<string> terms, CancellationToken ct)
    {
        var expr = string.Join(" OR ", terms.Select(t => "(" + string.Join(" AND ", Trigrams(t)!.Select(Quote)) + ")"));
        using var conn = Open(ShardPath(day), readOnly: true);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT rowid FROM fts WHERE fts MATCH $m ORDER BY rowid";
        cmd.Parameters.AddWithValue("$m", expr);
        var list = new List<long>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if ((list.Count & 4095) == 0) ct.ThrowIfCancellationRequested();
            list.Add(r.GetInt64(0));
        }
        return list;
    }

    static string Quote(string t) => "\"" + t.Replace("\"", "\"\"") + "\"";

    /// <summary>
    /// 關鍵字的 3 字片段（不必全部：每隔 3 個字取一個，再加上最後一個，已涵蓋所有字元）。
    /// 少於 3 字或含 surrogate（FTS5 以字碼計算，與 .NET 的 char 不一致）時為 null。
    /// </summary>
    public static IReadOnlyList<string>? Trigrams(string term)
    {
        if (term.Length < 3 || term.Any(char.IsSurrogate)) return null;
        var list = new List<string>();
        for (int i = 0; i + 3 <= term.Length; i += 3) list.Add(term.Substring(i, 3));
        list.Add(term[^3..]);
        return list.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>已建好的天數。</summary>
    public int CountBuilt(IReadOnlyList<string> days, string contentVersion) => days.Count(d => IsBuilt(d, contentVersion));

    /// <summary>
    /// 依日期建立尚未建好的分片（背景執行；可以取消，取消時已建好的分片保留）。
    /// 建立前估計磁碟用量，會超過原始 LOG 的 5 倍時停止，其餘日期以掃描搜尋。
    /// </summary>
    /// <param name="days">日期資料夾與該日原始 LOG 的大小。</param>
    public IndexBuildResult Build(IReadOnlyList<(string Day, long RawBytes)> days, string contentVersion, long rawBytes,
        IProgress<IndexBuildProgress>? progress, CancellationToken ct)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Directory.CreateDirectory(Folder);
        int built = days.Count(d => IsBuilt(d.Day, contentVersion));
        progress?.Report(new(built, days.Count, null));
        bool limit = false;
        foreach (var (day, dayRaw) in days)
        {
            ct.ThrowIfCancellationRequested();
            if (IsBuilt(day, contentVersion)) continue;
            if (CaseBytes() + dayRaw * EstimatedIndexRatio > rawBytes * DiskLimitRatio)
            {
                limit = true;
                break;
            }
            progress?.Report(new(built, days.Count, day));
            if (BuildShard(day, contentVersion, ct)) built++;
        }
        progress?.Report(new(built, days.Count, null));
        return new(built, days.Count, limit, clock.Elapsed);
    }

    /// <summary>案件資料夾的磁碟用量（不含 SQLite 的 WAL 暫存檔，它們在關閉時併回）。</summary>
    public long CaseBytes() =>
        Directory.EnumerateFiles(caseFolder, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("-wal", StringComparison.Ordinal) && !f.EndsWith("-shm", StringComparison.Ordinal))
            .Sum(f => new FileInfo(f).Length);

    bool BuildShard(string day, string contentVersion, CancellationToken ct)
    {
        var path = ShardPath(day);
        var tmp = path + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);
        try
        {
            using (var shard = Open(tmp, readOnly: false))
            using (var source = Open(caseDbPath, readOnly: true))
            {
                Exec(shard, $"""
                    PRAGMA journal_mode=OFF;
                    PRAGMA synchronous=OFF;
                    CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                    CREATE VIRTUAL TABLE fts USING fts5(norm, content='', tokenize='trigram', detail='none');
                    CREATE VIRTUAL TABLE vocab USING fts5vocab(fts, 'row');
                    """);
                using var tx = shard.BeginTransaction();
                using var insert = shard.CreateCommand();
                insert.CommandText = "INSERT INTO fts(rowid, norm) VALUES($s, $n)";
                var s = insert.Parameters.Add(new SqliteParameter { ParameterName = "$s" });
                var n = insert.Parameters.Add(new SqliteParameter { ParameterName = "$n" });
                insert.Prepare();
                using var read = source.CreateCommand();
                read.CommandText = """
                    SELECT s.seq, s.norm FROM event_search s JOIN events e ON e.seq = s.seq JOIN files f ON f.file_id = e.file_id
                    WHERE f.day_folder = $d
                    """;
                read.Parameters.AddWithValue("$d", day);
                using (var r = read.ExecuteReader())
                {
                    int k = 0;
                    while (r.Read())
                    {
                        if ((++k & 4095) == 0) ct.ThrowIfCancellationRequested();
                        (s.Value, n.Value) = (r.GetInt64(0), r.GetString(1));
                        insert.ExecuteNonQuery();
                    }
                }
                using var meta = shard.CreateCommand();
                meta.CommandText = "INSERT INTO meta VALUES('format', $f), ('content_version', $v), ('built_at', $t)";
                meta.Parameters.AddWithValue("$f", FormatVersion);
                meta.Parameters.AddWithValue("$v", contentVersion);
                meta.Parameters.AddWithValue("$t", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
                meta.ExecuteNonQuery();
                tx.Commit();
                Exec(shard, "INSERT INTO fts(fts) VALUES('optimize');");
            }
            try
            {
                if (File.Exists(path)) File.Delete(path); // 舊版本的分片
                File.Move(tmp, path);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false; // 舊分片正被搜尋使用：下次再建立
            }
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    /// <summary>刪除全文索引（之後可以重新建立）。</summary>
    public void Delete()
    {
        if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true);
    }

    static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
