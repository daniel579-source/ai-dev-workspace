using System.Globalization;
using System.Text.Json;
using LogTrace.Engine.Events;
using Microsoft.Data.Sqlite;

namespace LogTrace.Engine.Storage;

/// <summary>SQLite 實作（ADR-0011：case.db 屬於不可重建的資料）。</summary>
public sealed class SqliteEventStore : IEventStore
{
    readonly SqliteConnection _conn;

    public SqliteEventStore(string databasePath)
    {
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false, // 關閉案件後不留下連線，案件資料夾可以安全複製（ADR-0011 風險）
        }.ToString());
        _conn.Open();
        Exec("""
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS files(
                file_id INTEGER PRIMARY KEY,
                module TEXT NOT NULL, day_folder TEXT NOT NULL, logger TEXT NOT NULL,
                rel_path TEXT NOT NULL UNIQUE, size INTEGER NOT NULL, sha256 TEXT NOT NULL,
                encoding TEXT NOT NULL, event_count INTEGER NOT NULL, unparsable INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS events(
                seq INTEGER PRIMARY KEY,
                file_id INTEGER NOT NULL REFERENCES files(file_id),
                line_start INTEGER NOT NULL, line_end INTEGER NOT NULL,
                byte_offset INTEGER NOT NULL, byte_len INTEGER NOT NULL,
                ts INTEGER NOT NULL, raw_ts TEXT NOT NULL,
                level TEXT NOT NULL, logger TEXT NOT NULL, message TEXT NOT NULL, flags INTEGER NOT NULL);
            """);
    }

    /// <summary>案件資料庫的結構版本；與程式不符時拒絕開啟（第一階段開發中，不做移轉）。</summary>
    public const int SchemaVersion = 2;

    public void SaveInfo(CaseInfo info)
    {
        using var tx = _conn.BeginTransaction();
        SetMeta("schema_version", SchemaVersion.ToString(CultureInfo.InvariantCulture));
        SetMeta("name", info.Name);
        SetMeta("plant", info.PlantName);
        SetMeta("module_order", JsonSerializer.Serialize(info.ModuleOrder));
        SetMeta("known_loggers", JsonSerializer.Serialize(info.KnownLoggers));
        SetMeta("created_at", info.CreatedAt.ToString("o", CultureInfo.InvariantCulture));
        tx.Commit();
    }

    public CaseInfo LoadInfo()
    {
        if (TryGetMeta("schema_version") != SchemaVersion.ToString(CultureInfo.InvariantCulture))
            throw new InvalidDataException("案件資料庫的版本與程式不符，請重新建立案件並匯入。");
        return new(
            GetMeta("name"),
            GetMeta("plant"),
            JsonSerializer.Deserialize<string[]>(GetMeta("module_order")) ?? [],
            JsonSerializer.Deserialize<string[]>(GetMeta("known_loggers")) ?? [],
            DateTimeOffset.Parse(GetMeta("created_at"), CultureInfo.InvariantCulture));
    }

    public void SaveImport(IReadOnlyList<StoredFile> files, IReadOnlyList<LogEvent> events)
    {
        using var tx = _conn.BeginTransaction();
        var fileIds = new Dictionary<string, long>(StringComparer.Ordinal);
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO files(module, day_folder, logger, rel_path, size, sha256, encoding, event_count, unparsable) VALUES($m,$d,$l,$p,$s,$h,$e,$n,$u) RETURNING file_id";
            foreach (var f in files)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$m", f.Module);
                cmd.Parameters.AddWithValue("$d", f.DayFolder);
                cmd.Parameters.AddWithValue("$l", f.Logger);
                cmd.Parameters.AddWithValue("$p", f.RelativePath);
                cmd.Parameters.AddWithValue("$s", f.Size);
                cmd.Parameters.AddWithValue("$h", f.Sha256);
                cmd.Parameters.AddWithValue("$e", f.Encoding);
                cmd.Parameters.AddWithValue("$n", f.EventCount);
                cmd.Parameters.AddWithValue("$u", f.Unparsable);
                fileIds[f.RelativePath] = (long)cmd.ExecuteScalar()!;
            }
        }
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO events VALUES($seq,$f,$ls,$le,$bo,$bl,$ts,$raw,$lv,$lg,$msg,$fl)";
            var p = new[] { "$seq", "$f", "$ls", "$le", "$bo", "$bl", "$ts", "$raw", "$lv", "$lg", "$msg", "$fl" }
                .Select(n => cmd.Parameters.Add(new SqliteParameter { ParameterName = n })).ToArray();
            cmd.Prepare();
            foreach (var e in events)
            {
                p[0].Value = e.Sequence;
                p[1].Value = fileIds[e.Source.RelativePath];
                p[2].Value = e.Source.LineStart;
                p[3].Value = e.Source.LineEnd;
                p[4].Value = e.Source.ByteOffset;
                p[5].Value = e.Source.ByteLength;
                p[6].Value = e.Timestamp.Ticks;
                p[7].Value = e.RawTimestamp;
                p[8].Value = e.Level;
                p[9].Value = e.Logger;
                p[10].Value = e.Message;
                p[11].Value = (int)e.Flags;
                cmd.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }

    public long GetEventCount()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM events";
        return (long)cmd.ExecuteScalar()!;
    }

    public IReadOnlyList<LogEvent> GetEvents(long fromSequence, int maxCount)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT e.seq, f.module, f.day_folder, f.rel_path, e.line_start, e.line_end, e.byte_offset, e.byte_len,
                   e.ts, e.raw_ts, e.level, e.logger, e.message, e.flags
            FROM events e JOIN files f ON f.file_id = e.file_id
            WHERE e.seq >= $from ORDER BY e.seq LIMIT $n
            """;
        cmd.Parameters.AddWithValue("$from", fromSequence);
        cmd.Parameters.AddWithValue("$n", maxCount);
        using var r = cmd.ExecuteReader();
        var list = new List<LogEvent>();
        while (r.Read())
        {
            string module = r.GetString(1), day = r.GetString(2), path = r.GetString(3);
            int lineStart = r.GetInt32(4);
            list.Add(new LogEvent(
                r.GetInt64(0),
                new StableEventId(module, day, path, lineStart),
                new DateTime(r.GetInt64(8)),
                r.GetString(9),
                r.GetString(10),
                module,
                r.GetString(11),
                r.GetString(12),
                new EventSource(path, lineStart, r.GetInt32(5), r.GetInt64(6), r.GetInt32(7)),
                (EventFlags)r.GetInt32(13)));
        }
        return list;
    }

    public IReadOnlyList<StoredFile> GetFiles()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT module, day_folder, logger, rel_path, size, sha256, encoding, event_count, unparsable FROM files ORDER BY file_id";
        using var r = cmd.ExecuteReader();
        var list = new List<StoredFile>();
        while (r.Read())
            list.Add(new StoredFile(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetString(5),
                r.GetString(6), r.GetInt32(7), r.GetBoolean(8)));
        return list;
    }

    public void Dispose() => _conn.Dispose();

    void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    void SetMeta(string key, string value)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO meta VALUES($k,$v)";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    string GetMeta(string key) => TryGetMeta(key) ?? throw new InvalidDataException($"案件資料缺少 {key}");

    string? TryGetMeta(string key)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }
}
