using System.Globalization;
using System.Text.Json;
using LogTrace.Engine.Events;
using LogTrace.Engine.Parsing;
using LogTrace.Engine.Plants;
using LogTrace.Engine.Search;
using Microsoft.Data.Sqlite;

namespace LogTrace.Engine.Storage;

/// <summary>SQLite 實作（ADR-0011：case.db 屬於不可重建的資料）。</summary>
public sealed class SqliteEventStore : IEventStore
{
    /// <summary>案件資料庫的結構版本；與程式不符時拒絕開啟（第一階段開發中，不做移轉）。</summary>
    public const int SchemaVersion = 3;

    readonly SqliteConnection _conn;
    readonly string _path;

    public SqliteEventStore(string databasePath, bool readOnly = false)
    {
        _path = databasePath;
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
            CREATE TABLE IF NOT EXISTS event_fields(
                seq INTEGER NOT NULL, kind TEXT NOT NULL, value TEXT NOT NULL COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS event_fields_kind_value ON event_fields(kind, value);
            CREATE INDEX IF NOT EXISTS event_fields_seq ON event_fields(seq);
            CREATE TABLE IF NOT EXISTS event_effects(
                seq INTEGER NOT NULL, effect TEXT NOT NULL, params TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS event_effects_seq ON event_effects(seq);
            CREATE TABLE IF NOT EXISTS event_search(seq INTEGER PRIMARY KEY, norm TEXT NOT NULL);
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
        SetMeta("parse_rules", JsonSerializer.Serialize(info.Plant.ParseRules));
        SetMeta("presence_signal", info.Plant.PresenceSignal);
        SetMeta("mask_patterns", JsonSerializer.Serialize(info.Plant.MaskPatterns));
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
            // parse_rules 為 #23 加入；之前建立的案件使用初始規則
            ParseRules = TryGetMeta("parse_rules") is { } pr ? JsonSerializer.Deserialize<ParseRule[]>(pr) ?? [] : PlantConfig.DefaultParseRules,
            PresenceSignal = TryGetMeta("presence_signal") ?? PlantConfig.Default.PresenceSignal,
            MaskPatterns = TryGetMeta("mask_patterns") is { } mp ? JsonSerializer.Deserialize<string[]>(mp) ?? [] : TextMasker.DefaultPatterns,
        };
        return new(GetMeta("name"), plant, ParseIso(GetMeta("created_at")));
    }

    public long NextImportId()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT coalesce(max(import_id), 0) + 1 FROM imports";
        return (long)cmd.ExecuteScalar()!;
    }

    public void SaveImport(ImportRecord import, IReadOnlyList<StoredFile> files, IReadOnlyList<LogEvent> events,
        IReadOnlyList<EventField> fields, IReadOnlyList<EventEffect> effects, IReadOnlyList<AuditEntry> audit)
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
        Exec("DELETE FROM events; DELETE FROM files; DELETE FROM event_fields; DELETE FROM event_effects; DELETE FROM event_search;");

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
        InsertFields(fields);
        InsertEffects(effects);
        InsertSearchText(events.Select(e => (e.Sequence, e.Message)));
        foreach (var a in audit) InsertAudit(a);
        tx.Commit();
    }

    /// <summary>正規化的搜尋用文字（ADR-0011 第 2 點：匯入時產生）。</summary>
    void InsertSearchText(IEnumerable<(long Sequence, string Message)> rows)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO event_search VALUES($s,$n)";
        var s = cmd.Parameters.Add(new SqliteParameter { ParameterName = "$s" });
        var n = cmd.Parameters.Add(new SqliteParameter { ParameterName = "$n" });
        cmd.Prepare();
        foreach (var (seq, message) in rows)
        {
            (s.Value, n.Value) = (seq, TextNormalizer.Normalize(message));
            cmd.ExecuteNonQuery();
        }
    }

    public int EnsureSearchText()
    {
        using var count = _conn.CreateCommand();
        count.CommandText = "SELECT count(*) FROM events e WHERE NOT EXISTS (SELECT 1 FROM event_search s WHERE s.seq = e.seq)";
        if ((long)count.ExecuteScalar()! == 0) return 0;
        var missing = new List<(long, string)>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT seq, message FROM events e WHERE NOT EXISTS (SELECT 1 FROM event_search s WHERE s.seq = e.seq)";
            using var r = cmd.ExecuteReader();
            while (r.Read()) missing.Add((r.GetInt64(0), r.GetString(1)));
        }
        using var tx = _conn.BeginTransaction();
        InsertSearchText(missing);
        tx.Commit();
        return missing.Count;
    }

    public IReadOnlyList<long> Search(SearchQuery query, CancellationToken ct)
    {
        // 事件序號依時間排序，所以依序號範圍分片、平行掃描（ADR-0011 第 1 點：依日平行；這裡把一天也再分片）。
        // 每片使用自己的唯讀連線；先以搜尋用文字篩選，再取符合的事件比對其他條件。結果依事件序號排序。
        long max;
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT coalesce(max(seq), 0) FROM events";
            max = (long)cmd.ExecuteScalar()!;
        }
        int parts = Math.Max(1, Math.Min(Environment.ProcessorCount, (int)(max / 20_000) + 1));
        long size = max / parts + 1;
        var results = new System.Collections.Concurrent.ConcurrentBag<List<long>>();
        Parallel.For(0, parts, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount }, part =>
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = _path, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
            }.ToString());
            conn.Open();
            results.Add(SearchRange(conn, query, part * size + 1, Math.Min(max, (part + 1) * size), ct));
        });
        var all = results.SelectMany(x => x).ToList();
        all.Sort();
        return all;
    }

    static List<long> SearchRange(SqliteConnection conn, SearchQuery q, long firstSeq, long lastSeq, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        var where = new List<string> { "s.seq BETWEEN $first AND $last" };
        cmd.Parameters.AddWithValue("$first", firstSeq);
        cmd.Parameters.AddWithValue("$last", lastSeq);
        if (q.From is { } from) { where.Add("e.ts >= $from"); cmd.Parameters.AddWithValue("$from", from.Ticks); }
        if (q.To is { } to) { where.Add("e.ts < $to"); cmd.Parameters.AddWithValue("$to", to.Ticks); }
        int n = 0;
        string P(object value)
        {
            var name = "$p" + n++;
            cmd.Parameters.AddWithValue(name, value);
            return name;
        }
        string In(IReadOnlyList<string> values) => string.Join(",", values.Select(v => P(v)));
        string Has(string term) => $"instr(s.norm, {P(TextNormalizer.Normalize(term))}) > 0";

        // 先以不分大小寫的正規化文字篩選；區分大小寫時在下面再以原文確認（「不包含」也改在下面判斷）
        foreach (var t in q.AllTerms) where.Add(Has(t));
        if (q.AnyTerms.Count > 0) where.Add("(" + string.Join(" OR ", q.AnyTerms.Select(Has)) + ")");
        if (!q.CaseSensitive && q.NoneTerms.Count > 0) where.Add("NOT (" + string.Join(" OR ", q.NoneTerms.Select(Has)) + ")");
        if (q.Modules.Count > 0) where.Add($"f.module IN ({In(q.Modules)})");
        if (q.Loggers.Count > 0) where.Add($"e.logger IN ({In(q.Loggers)})");
        if (q.Levels.Count > 0) where.Add($"e.level IN ({In(q.Levels)})");
        if (q.Flags != EventFlags.None) where.Add($"(e.flags & {P((int)q.Flags)}) != 0");
        if (q.FieldKinds.Count > 0 && q.FieldValues.Count > 0)
            where.Add($"e.seq IN (SELECT seq FROM event_fields WHERE kind IN ({In(q.FieldKinds)}) AND value IN ({In(q.FieldValues)}))");

        cmd.CommandText = $"""
            SELECT e.seq, e.message FROM event_search s CROSS JOIN events e ON e.seq = s.seq JOIN files f ON f.file_id = e.file_id
            WHERE {string.Join(" AND ", where)} ORDER BY s.seq
            """;
        var list = new List<long>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (q.CaseSensitive && !q.MatchesText(r.GetString(1))) continue;
            list.Add(r.GetInt64(0));
        }
        return list;
    }

    public void ReplaceFields(IReadOnlyList<EventField> fields, IReadOnlyList<EventEffect> effects, CaseInfo info, AuditEntry audit)
    {
        using var tx = _conn.BeginTransaction();
        Exec("DELETE FROM event_fields; DELETE FROM event_effects;");
        InsertFields(fields);
        InsertEffects(effects);
        SetMeta("parse_rules", JsonSerializer.Serialize(info.Plant.ParseRules));
        InsertAudit(audit);
        tx.Commit();
    }

    void InsertFields(IReadOnlyList<EventField> fields)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO event_fields VALUES($s,$k,$v)";
        var s = cmd.Parameters.Add(new SqliteParameter { ParameterName = "$s" });
        var k = cmd.Parameters.Add(new SqliteParameter { ParameterName = "$k" });
        var v = cmd.Parameters.Add(new SqliteParameter { ParameterName = "$v" });
        cmd.Prepare();
        foreach (var f in fields)
        {
            (s.Value, k.Value, v.Value) = (f.Sequence, f.Kind, f.Value);
            cmd.ExecuteNonQuery();
        }
    }

    void InsertEffects(IReadOnlyList<EventEffect> effects)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO event_effects VALUES($s,$e,$p)";
        var s = cmd.Parameters.Add(new SqliteParameter { ParameterName = "$s" });
        var e = cmd.Parameters.Add(new SqliteParameter { ParameterName = "$e" });
        var p = cmd.Parameters.Add(new SqliteParameter { ParameterName = "$p" });
        cmd.Prepare();
        foreach (var x in effects)
        {
            (s.Value, e.Value, p.Value) = (x.Sequence, x.Effect, JsonSerializer.Serialize(x.Params));
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<EventEffect> GetEffects()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT seq, effect, params FROM event_effects ORDER BY seq, rowid";
        using var r = cmd.ExecuteReader();
        var list = new List<EventEffect>();
        while (r.Read())
        {
            var p = JsonSerializer.Deserialize<Dictionary<string, string[]>>(r.GetString(2)) ?? [];
            list.Add(new EventEffect(r.GetInt64(0), r.GetString(1),
                p.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.Ordinal)));
        }
        return list;
    }

    public IReadOnlyList<EventField> GetFields(long sequence)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT seq, kind, value FROM event_fields WHERE seq = $s ORDER BY rowid";
        cmd.Parameters.AddWithValue("$s", sequence);
        using var r = cmd.ExecuteReader();
        var list = new List<EventField>();
        while (r.Read()) list.Add(new EventField(r.GetInt64(0), r.GetString(1), r.GetString(2)));
        return list;
    }

    public IReadOnlyList<(string Kind, string Value, long Count)> GetFieldValueCounts(IReadOnlyList<string> kinds)
    {
        using var cmd = _conn.CreateCommand();
        var names = kinds.Select((k, i) => "$k" + i).ToList();
        cmd.CommandText = $"""
            SELECT kind, value, count(DISTINCT seq) FROM event_fields WHERE kind IN ({string.Join(",", names)})
            GROUP BY kind, value COLLATE NOCASE ORDER BY kind, count(DISTINCT seq) DESC, value
            """;
        for (int i = 0; i < kinds.Count; i++) cmd.Parameters.AddWithValue(names[i], kinds[i]);
        using var r = cmd.ExecuteReader();
        var list = new List<(string, string, long)>();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1), r.GetInt64(2)));
        return list;
    }

    public IReadOnlyList<long> FindSequences(IReadOnlyList<string> kinds, IReadOnlyList<string> values)
    {
        if (kinds.Count == 0 || values.Count == 0) return [];
        using var cmd = _conn.CreateCommand();
        var kn = kinds.Select((_, i) => "$k" + i).ToList();
        var vn = values.Select((_, i) => "$v" + i).ToList();
        cmd.CommandText = $"""
            SELECT DISTINCT seq FROM event_fields
            WHERE kind IN ({string.Join(",", kn)}) AND value IN ({string.Join(",", vn)}) ORDER BY seq
            """;
        for (int i = 0; i < kinds.Count; i++) cmd.Parameters.AddWithValue(kn[i], kinds[i]);
        for (int i = 0; i < values.Count; i++) cmd.Parameters.AddWithValue(vn[i], values[i]);
        using var r = cmd.ExecuteReader();
        var list = new List<long>();
        while (r.Read()) list.Add(r.GetInt64(0));
        return list;
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
