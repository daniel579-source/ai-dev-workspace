using Microsoft.Data.Sqlite;

namespace TechValidation.Core;

/// <summary>
/// 案件資料夾（ADR-0011 第 2 點）：
/// 不可重建：case.db、raw\；可重建：index\（依日分片）。
/// </summary>
public sealed class CaseStore
{
    public string Root { get; }
    public string CaseDb => Path.Combine(Root, "case.db");
    public string RawDir => Path.Combine(Root, "raw");
    public string IndexDir => Path.Combine(Root, "index");

    public CaseStore(string root) => Root = Path.GetFullPath(root);

    public string ShardDb(int day) => Path.Combine(IndexDir, $"{day}.db");
    public string FlatFile(int day) => Path.Combine(IndexDir, $"{day}.norm");

    public static string ConnStr(string path, bool readOnly = false) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();

    public SqliteConnection OpenCase(bool readOnly = false) => Open(CaseDb, readOnly);

    public SqliteConnection OpenShard(int day, bool readOnly = false) => Open(ShardDb(day), readOnly);

    public static SqliteConnection Open(string path, bool readOnly = false)
    {
        var c = new SqliteConnection(ConnStr(path, readOnly));
        c.Open();
        Exec(c, "PRAGMA cache_size=-65536; PRAGMA temp_store=MEMORY; PRAGMA mmap_size=1073741824;");
        return c;
    }

    public static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public static T Scalar<T>(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (T)Convert.ChangeType(cmd.ExecuteScalar()!, typeof(T));
    }

    public void CreateCaseSchema()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(RawDir);
        Directory.CreateDirectory(IndexDir);
        using var c = OpenCase();
        Exec(c, """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT);
            CREATE TABLE IF NOT EXISTS files(
                file_id INTEGER PRIMARY KEY, day INTEGER NOT NULL, module TEXT NOT NULL, logger TEXT NOT NULL,
                rel_path TEXT NOT NULL, stream_order INTEGER NOT NULL, encoding TEXT, size INTEGER, sha256 TEXT, unparsable INTEGER DEFAULT 0);
            CREATE TABLE IF NOT EXISTS loggers(logger_id INTEGER PRIMARY KEY, module TEXT NOT NULL, name TEXT NOT NULL, UNIQUE(module, name));
            CREATE TABLE IF NOT EXISTS events(
                seq INTEGER PRIMARY KEY,
                ts INTEGER NOT NULL,
                file_id INTEGER NOT NULL,
                line_start INTEGER NOT NULL,
                line_end INTEGER NOT NULL,
                byte_offset INTEGER NOT NULL,
                byte_len INTEGER NOT NULL,
                level INTEGER NOT NULL,
                logger_id INTEGER NOT NULL,
                module_id INTEGER NOT NULL,
                flags INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS refs(kind INTEGER NOT NULL, value INTEGER NOT NULL, seq INTEGER NOT NULL, PRIMARY KEY(kind, value, seq)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS stations(station_id INTEGER PRIMARY KEY, name TEXT UNIQUE NOT NULL);
            CREATE TABLE IF NOT EXISTS status_names(status_id INTEGER PRIMARY KEY, name TEXT UNIQUE NOT NULL);
            CREATE TABLE IF NOT EXISTS station_changes(seq INTEGER PRIMARY KEY, station_id INTEGER NOT NULL, status_id INTEGER NOT NULL, value INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS snapshots(seq INTEGER PRIMARY KEY, data BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS audit(id INTEGER PRIMARY KEY, at TEXT NOT NULL, action TEXT NOT NULL, target TEXT);
            """);
    }

    public void CreateEventIndexes()
    {
        using var c = OpenCase();
        Exec(c, "CREATE INDEX IF NOT EXISTS ix_events_ts ON events(ts);");
    }

    public static void CreateShardSchema(SqliteConnection c, string ftsDetail, bool withFts)
    {
        Exec(c, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        Exec(c, "CREATE TABLE IF NOT EXISTS norm(seq INTEGER PRIMARY KEY, ntext TEXT NOT NULL);");
        if (withFts) CreateFts(c, ftsDetail);
    }

    public static void CreateFts(SqliteConnection c, string ftsDetail) =>
        Exec(c, $"CREATE VIRTUAL TABLE IF NOT EXISTS fts USING fts5(ntext, content='norm', content_rowid='seq', tokenize='trigram', detail={ftsDetail});");

    public IEnumerable<int> Days()
    {
        using var c = OpenCase(readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT day FROM files ORDER BY day";
        using var r = cmd.ExecuteReader();
        var list = new List<int>();
        while (r.Read()) list.Add(r.GetInt32(0));
        return list;
    }

    /// <summary>關閉所有連線，讓 WAL 寫回主檔（ADR-0011 風險：複製案件資料夾不完整）。</summary>
    public static void CloseAll() => SqliteConnection.ClearAllPools();

    public long DirectorySize(string dir) =>
        Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;

    public static string SqliteVersion(SqliteConnection c) => Scalar<string>(c, "select sqlite_version()");

    /// <summary>T-01：FTS5 trigram 是否可用。</summary>
    public static bool TrigramAvailable(out string detail)
    {
        try
        {
            using var c = new SqliteConnection("Data Source=:memory:");
            c.Open();
            Exec(c, "CREATE VIRTUAL TABLE t USING fts5(x, tokenize='trigram');");
            Exec(c, "INSERT INTO t VALUES('station_status_change => 在席 = on');");
            var n = Scalar<long>(c, "SELECT count(*) FROM t WHERE t MATCH '\"tus_cha\"'");
            detail = $"sqlite {SqliteVersion(c)}, trigram match={n}";
            return n == 1;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }
    }
}
