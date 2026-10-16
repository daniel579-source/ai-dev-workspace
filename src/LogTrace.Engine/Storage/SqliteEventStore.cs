using System.Globalization;
using System.Text.Json;
using LogTrace.Engine.Events;
using LogTrace.Engine.Plants;
using Microsoft.Data.Sqlite;

namespace LogTrace.Engine.Storage;

/// <summary>SQLite 實作（ADR-0011：case.db 屬於不可重建的資料）。</summary>
public sealed class SqliteEventStore : IEventStore
{
    /// <summary>案件資料庫的結構版本；與程式不符時拒絕開啟（第一階段開發中，不做移轉）。</summary>
    public const int SchemaVersion = 3;

    readonly SqliteConnection _conn;

    public SqliteEventStore(string databasePath, bool readOnly = false)
    {
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false, // 關閉案件後不留下連線，案件資料夾可以安全複製（ADR-0011 風險）
        }.ToString());
        _conn.Open();
        if (readOnly) return;
        Exec("""
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS imports(
                import_id INTEGER PRIMARY KEY, time TEXT NOT NULL, source_path TEXT NOT NULL, source_kind TEXT NOT NULL,
                archive_sha256 TEXT, archive_copy TEXT);
            CREATE TABLE IF NOT EXISTS files(
                file_id INTEGER PRIMARY KEY,
                rel_path TEXT NOT NULL, version INTEGER NOT NULL, storage_path TEXT NOT NULL, active INTEGER NOT NULL,
                module TEXT NOT NULL, day_folder TEXT NOT NULL, logger TEXT NOT NULL, stream_key TEXT NOT NULL,
                size INTEGER NOT NULL, sha256 TEXT NOT NULL, encoding TEXT NOT NULL, event_count INTEGER NOT NULL, unparsable INTEGER NOT NULL,
                import_id INTEGER NOT NULL, source_path TEXT NOT NULL, imported_at TEXT NOT NULL,
                UNIQUE(rel_path, version));
            CREATE TABLE IF NOT EXISTS events(
                seq INTEGER PRIMARY KEY,
                file_id INTEGER NOT NULL REFERENCES files(file_id),
                line_start INTEGER NOT NULL, line_end INTEGER NOT NULL,
                byte_offset INTEGER NOT NULL, byte_len INTEGER NOT NULL,
                ts INTEGER NOT NULL, raw_ts TEXT NOT NULL,
                level TEXT NOT NULL, logger TEXT NOT NULL, message TEXT NOT NULL, flags INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS audit(
                audit_id INTEGER PRIMARY KEY, time TEXT NOT NULL, user TEXT NOT NULL,
                action TEXT NOT NULL, target TEXT NOT NULL, detail TEXT NOT NULL);
            """);
    }

    public void SaveInfo(CaseInfo info)
    {
        using var tx = _conn.BeginTransaction();
        SetMeta("schema_version", SchemaVersion.ToString(CultureInfo.InvariantCulture));
        SetMeta("name", info.Name);
        SetMeta("plant", info.Plant.Name);
        SetMeta("plant_revision", info.Plant.Revision.ToString(CultureInfo.InvariantCulture));
        SetMeta("module_order", JsonSerializer.Serialize(info.Plant.ModuleOrder));
        SetMeta("known_loggers", JsonSerializer.Serialize(info.Plant.KnownLoggers));
        SetMeta("encodings", JsonSerializer.Serialize(info.Plant.Encodings));
        SetMeta("created_at", Iso(info.CreatedAt));
        tx.Commit();
    }

    public CaseInfo LoadInfo()
    {
        if (TryGetMeta("schema_version") != SchemaVersion.ToString(CultureInfo.InvariantCulture))
            throw new InvalidDataException("案件資料庫的版本與程式不符，請重新建立案件並匯入。");
        // plant_revision、encodings 為 #34 加入；之前建立的案件沒有，視為內建預設（修訂 0、自動判斷編碼）
        var plant = new PlantConfig
        {
            Name = GetMeta("plant"),
            Revision = int.TryParse(TryGetMeta("plant_revision"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : 0,
            ModuleOrder = JsonSerializer.Deserialize<string[]>(GetMeta("module_order")) ?? [],
            KnownLoggers = JsonSerializer.Deserialize<string[]>(GetMeta("known_loggers")) ?? [],
            Encodings = TryGetMeta("encodings") is { } enc ? JsonSerializer.Deserialize<EncodingRule[]>(enc) ?? [] : [],
        };
        return new(GetMeta("name"), plant, ParseIso(GetMeta("created_at")));
    }

    public long NextImportId()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT coalesce(max(import_id), 0) + 1 FROM imports";
        return (long)cmd.ExecuteScalar()!;
    }

    public void SaveImport(ImportRecord import, IReadOnlyList<StoredFile> files, IReadOnlyList<LogEvent> events, IReadOnlyList<AuditEntry> audit)
    {
        using var tx = _conn.BeginTransaction();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO imports VALUES($id,$t,$p,$k,$h,$c)";
            cmd.Parameters.AddWithValue("$id", import.Id);
            cmd.Parameters.AddWithValue("$t", Iso(import.Time));
            cmd.Parameters.AddWithValue("$p", import.SourcePath);
            cmd.Parameters.AddWithValue("$k", import.SourceKind);
            cmd.Parameters.AddWithValue("$h", (object?)import.ArchiveSha256 ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$c", (object?)import.ArchiveCopy ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        Exec("DELETE FROM events; DELETE FROM files;");

        var activeIds = new Dictionary<string, long>(StringComparer.Ordinal);
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO files(rel_path, version, storage_path, active, module, day_folder, logger, stream_key,
                                  size, sha256, encoding, event_count, unparsable, import_id, source_path, imported_at)
                VALUES($rp,$v,$sp,$a,$m,$d,$l,$sk,$s,$h,$e,$n,$u,$i,$src,$at) RETURNING file_id
                """;
            foreach (var f in files)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$rp", f.RelativePath);
                cmd.Parameters.AddWithValue("$v", f.Version);
                cmd.Parameters.AddWithValue("$sp", f.StoragePath);
                cmd.Parameters.AddWithValue("$a", f.Active);
                cmd.Parameters.AddWithValue("$m", f.Module);
                cmd.Parameters.AddWithValue("$d", f.DayFolder);
                cmd.Parameters.AddWithValue("$l", f.Logger);
                cmd.Parameters.AddWithValue("$sk", f.StreamKey);
                cmd.Parameters.AddWithValue("$s", f.Size);
                cmd.Parameters.AddWithValue("$h", f.Sha256);
                cmd.Parameters.AddWithValue("$e", f.Encoding);
                cmd.Parameters.AddWithValue("$n", f.EventCount);
                cmd.Parameters.AddWithValue("$u", f.Unparsable);
                cmd.Parameters.AddWithValue("$i", f.ImportId);
                cmd.Parameters.AddWithValue("$src", f.SourcePath);
                cmd.Parameters.AddWithValue("$at", Iso(f.ImportedAt));
                var id = (long)cmd.ExecuteScalar()!;
                if (f.Active) activeIds[f.RelativePath] = id;
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
                p[1].Value = activeIds[e.Source.RelativePath];
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
        foreach (var a in audit) InsertAudit(a);
        tx.Commit();
    }

    public void AddAudit(AuditEntry entry) => InsertAudit(entry);

    void InsertAudit(AuditEntry a)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO audit(time, user, action, target, detail) VALUES($t,$u,$a,$o,$d)";
        cmd.Parameters.AddWithValue("$t", Iso(a.Time));
        cmd.Parameters.AddWithValue("$u", a.User);
        cmd.Parameters.AddWithValue("$a", a.Action);
        cmd.Parameters.AddWithValue("$o", a.Target);
        cmd.Parameters.AddWithValue("$d", a.Detail);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<AuditEntry> GetAudit()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT time, user, action, target, detail FROM audit ORDER BY audit_id";
        using var r = cmd.ExecuteReader();
        var list = new List<AuditEntry>();
        while (r.Read()) list.Add(new AuditEntry(ParseIso(r.GetString(0)), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
        return list;
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
        cmd.CommandText = """
            SELECT rel_path, version, storage_path, active, module, day_folder, logger, stream_key,
                   size, sha256, encoding, event_count, unparsable, import_id, source_path, imported_at
            FROM files ORDER BY rel_path, version
            """;
        using var r = cmd.ExecuteReader();
        var list = new List<StoredFile>();
        while (r.Read())
            list.Add(new StoredFile(r.GetString(0), r.GetInt32(1), r.GetString(2), r.GetBoolean(3),
                r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7),
                r.GetInt64(8), r.GetString(9), r.GetString(10), r.GetInt32(11), r.GetBoolean(12),
                r.GetInt64(13), r.GetString(14), ParseIso(r.GetString(15))));
        return list;
    }

    public IReadOnlyList<ImportRecord> GetImports()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT import_id, time, source_path, source_kind, archive_sha256, archive_copy FROM imports ORDER BY import_id";
        using var r = cmd.ExecuteReader();
        var list = new List<ImportRecord>();
        while (r.Read())
            list.Add(new ImportRecord(r.GetInt64(0), ParseIso(r.GetString(1)), r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)));
        return list;
    }

    public void Dispose() => _conn.Dispose();

    static string Iso(DateTimeOffset t) => t.ToString("o", CultureInfo.InvariantCulture);
    static DateTimeOffset ParseIso(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture);

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
