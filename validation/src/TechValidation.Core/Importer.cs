using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace TechValidation.Core;

public sealed class ImportOptions
{
    /// <summary>只匯入這些日期（yyyyMMdd）；null 表示全部。</summary>
    public int[]? Days { get; init; }
    /// <summary>同時處理的日期數（依日平行）。</summary>
    public int DayParallelism { get; init; } = 1;
    /// <summary>單日內同時解析的檔案數。</summary>
    public int FileParallelism { get; init; } = Environment.ProcessorCount;
    public string FtsDetail { get; init; } = "full";
    /// <summary>基本匯入後是否建立 FTS5 trigram 索引（正式版在背景建立）。</summary>
    public bool BuildFts { get; init; } = true;
    /// <summary>是否另外寫出比較用的自訂順序檔（T-06）。</summary>
    public bool WriteFlatFile { get; init; } = true;
    public int SnapshotInterval { get; init; } = 20_000;
    public string[] ModuleOrder { get; init; } = ["EXE", "PLC", "Server"];
    public Action<string>? Log { get; init; }
}

public sealed class ImportReport
{
    public ConcurrentDictionary<string, double> PhaseSeconds { get; } = new();
    public long RawBytes;
    public long Events;
    public int Files;
    public int UnparsableFiles;
    public long TimeBackward, SameTimestamp, TimeInvalid, EncodingError, UnknownKind;
    public long OutOfDayEvents;
    public double BasicImportSeconds;
    public double FtsSeconds;
    public double SnapshotSeconds;
    public double TotalSeconds;

    internal void Add(string phase, double s) => PhaseSeconds.AddOrUpdate(phase, s, (_, v) => v + s);
}

/// <summary>
/// 匯入流程（ADR-0002、ADR-0004、ADR-0011）：
/// 複製原始檔＋雜湊 → 解析（依檔平行）→ 依排序規則編事件序號 → 寫入 case.db 與 index\ 分片 →（背景）FTS5 → 狀態快照。
/// </summary>
public sealed partial class Importer
{
    readonly CaseStore _store;
    readonly ImportOptions _opt;
    readonly ImportReport _report = new();
    readonly SemaphoreSlim _caseWriter = new(1, 1);
    readonly Dictionary<string, int> _stations = new();
    readonly Dictionary<string, int> _statuses = new();
    readonly Dictionary<(string, string), int> _loggers = new();

    public Importer(CaseStore store, ImportOptions opt)
    {
        _store = store;
        _opt = opt;
    }

    [GeneratedRegex(@"^(?<logger>.+)_(?<ts>\d{4}-\d{2}-\d{2} \d{2}_\d{2}_\d{2}\.\d{4})$")]
    private static partial Regex ArchiveName();

    [GeneratedRegex(@"^(?<module>.+)-(?<date>\d{4}-\d{2}-\d{2})$")]
    private static partial Regex ModuleDir();

    /// <summary>依「日期 → 模組」結構列出檔案（docs/log-format-notes.md 2.1）。</summary>
    public static List<LogFile> Discover(string logsRoot, int[]? days)
    {
        var files = new List<LogFile>();
        foreach (var dayDir in Directory.GetDirectories(logsRoot).Order(StringComparer.Ordinal))
        {
            var dayName = Path.GetFileName(dayDir);
            if (!int.TryParse(dayName, out var day) || dayName.Length != 8) continue;
            if (days is not null && !days.Contains(day)) continue;
            foreach (var modDir in Directory.GetDirectories(dayDir).Order(StringComparer.Ordinal))
            {
                var m = ModuleDir().Match(Path.GetFileName(modDir));
                if (!m.Success) continue;
                var module = m.Groups["module"].Value;
                var streams = new Dictionary<string, List<(string sortKey, string path)>>();
                void AddFile(string path, bool archive)
                {
                    var name = Path.GetFileNameWithoutExtension(path);
                    string logger = name, key = "~current";
                    if (archive)
                    {
                        var am = ArchiveName().Match(name);
                        if (am.Success) { logger = am.Groups["logger"].Value; key = am.Groups["ts"].Value; }
                    }
                    if (!streams.TryGetValue(logger, out var l)) streams[logger] = l = new();
                    l.Add((key, path));
                }
                foreach (var f in Directory.GetFiles(modDir, "*.txt")) AddFile(f, false);
                var arch = Path.Combine(modDir, "archives");
                if (Directory.Exists(arch)) foreach (var f in Directory.GetFiles(arch, "*.txt")) AddFile(f, true);

                foreach (var (logger, list) in streams.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    int order = 0;
                    foreach (var (_, path) in list.OrderBy(x => x.sortKey, StringComparer.Ordinal))
                    {
                        files.Add(new LogFile
                        {
                            Day = day,
                            Module = module,
                            Logger = logger,
                            FullPath = path,
                            RelativePath = Path.GetRelativePath(logsRoot, path),
                            StreamOrder = order++,
                            Size = new FileInfo(path).Length,
                        });
                    }
                }
            }
        }
        for (int i = 0; i < files.Count; i++) files[i].FileId = i + 1;
        return files;
    }

    public ImportReport Import(string logsRoot)
    {
        var total = Stopwatch.StartNew();
        _store.CreateCaseSchema();
        var files = Discover(logsRoot, _opt.Days);
        var byDay = files.GroupBy(f => f.Day).OrderBy(g => g.Key).ToList();
        _report.Files = files.Count;
        _report.RawBytes = files.Sum(f => f.Size);
        Log($"發現 {byDay.Count} 天、{files.Count} 個檔案、{_report.RawBytes / 1048576.0:F0} MB");

        var dayIndex = byDay.Select((g, i) => (g.Key, i)).ToDictionary(x => x.Key, x => x.i);
        Parallel.ForEach(byDay, new ParallelOptions { MaxDegreeOfParallelism = _opt.DayParallelism },
            g => ImportDay(g.Key, dayIndex[g.Key], g.ToList()));

        var sw = Stopwatch.StartNew();
        _store.CreateEventIndexes();
        _report.Add("events 索引", sw.Elapsed.TotalSeconds);
        _report.BasicImportSeconds = total.Elapsed.TotalSeconds;
        Log($"基本匯入完成：{_report.BasicImportSeconds:F1} 秒");

        if (_opt.BuildFts)
        {
            sw.Restart();
            Parallel.ForEach(byDay, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _opt.DayParallelism) },
                g => BuildFts(_store, g.Key, _opt.FtsDetail));
            _report.FtsSeconds = sw.Elapsed.TotalSeconds;
            Log($"FTS5 trigram（detail={_opt.FtsDetail}）：{_report.FtsSeconds:F1} 秒");
        }

        sw.Restart();
        Snapshots.Build(_store, _opt.SnapshotInterval);
        _report.SnapshotSeconds = sw.Elapsed.TotalSeconds;

        using (var c = _store.OpenCase())
        {
            CaseStore.Exec(c, $"INSERT OR REPLACE INTO meta VALUES('fts_detail','{_opt.FtsDetail}');");
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO audit(at, action, target) VALUES($at, 'import', $t)";
            cmd.Parameters.AddWithValue("$at", DateTime.Now.ToString("o"));
            cmd.Parameters.AddWithValue("$t", $"{files.Count} files");
            cmd.ExecuteNonQuery();
        }
        _report.TotalSeconds = total.Elapsed.TotalSeconds;
        return _report;
    }

    public static void BuildFts(CaseStore store, int day, string detail)
    {
        using var c = store.OpenShard(day);
        CaseStore.Exec(c, "DROP TABLE IF EXISTS fts;");
        CaseStore.CreateFts(c, detail);
        CaseStore.Exec(c, "INSERT INTO fts(fts) VALUES('rebuild');");
    }

    void ImportDay(int day, int dayIdx, List<LogFile> files)
    {
        var sw = Stopwatch.StartNew();
        var dayRaw = new ConcurrentDictionary<int, LogParser.FileResult>();
        var po = new ParallelOptions { MaxDegreeOfParallelism = _opt.FileParallelism };

        // 1+2. 讀取一次：雜湊、複製到 raw\、解析
        var hashes = new ConcurrentDictionary<int, string>();
        var tCopy = 0L; var tParse = 0L;
        Parallel.ForEach(files.OrderByDescending(f => f.Size), po, f =>
        {
            var t0 = Stopwatch.GetTimestamp();
            var bytes = File.ReadAllBytes(f.FullPath);
            hashes[f.FileId] = Convert.ToHexString(SHA256.HashData(bytes));
            var dest = Path.Combine(_store.RawDir, f.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, bytes);
            var t1 = Stopwatch.GetTimestamp();
            var res = LogParser.ParseFile(f, bytes);
            f.Encoding = res.Encoding;
            dayRaw[f.FileId] = res;
            Interlocked.Add(ref tCopy, t1 - t0);
            Interlocked.Add(ref tParse, Stopwatch.GetTimestamp() - t1);
        });
        _report.Add("讀檔＋雜湊＋複製（各執行緒合計）", tCopy / (double)Stopwatch.Frequency);
        _report.Add("解析＋正規化（各執行緒合計）", tParse / (double)Stopwatch.Frequency);
        _report.Add("讀檔到解析完成（實際經過）", sw.Elapsed.TotalSeconds);

        // 3. 輪替檔接續：檔案邊界上的時間倒退
        foreach (var stream in files.GroupBy(f => (f.Module, f.Logger)))
        {
            long prev = long.MinValue;
            foreach (var f in stream.OrderBy(f => f.StreamOrder))
            {
                var r = dayRaw[f.FileId];
                if (prev != long.MinValue)
                {
                    foreach (var ev in r.Events)
                    {
                        if ((ev.Flags & EventFlags.TimeInvalid) != 0) continue;
                        if (ev.Ticks < prev) ev.Flags |= EventFlags.TimeBackward;
                        break;
                    }
                }
                if (r.LastValidTicks != long.MinValue) prev = r.LastValidTicks;
            }
        }

        // 4. 排序（ADR-0004 第 5 點）：時間 → 模組順序 → Logger 名稱 → 輪替順序 → 行號
        sw.Restart();
        var fileRank = files
            .OrderBy(f => ModuleRank(f.Module)).ThenBy(f => f.Module, StringComparer.Ordinal)
            .ThenBy(f => f.Logger, StringComparer.Ordinal).ThenBy(f => f.StreamOrder)
            .Select((f, i) => (f.FileId, i)).ToDictionary(x => x.FileId, x => (long)x.i);
        var all = new List<ParsedEvent>(dayRaw.Values.Sum(r => r.Events.Count));
        foreach (var f in files) all.AddRange(dayRaw[f.FileId].Events);
        var keys = new (long, long)[all.Count];
        for (int i = 0; i < all.Count; i++) keys[i] = (all[i].Ticks, (fileRank[all[i].FileId] << 32) | (uint)all[i].LineStart);
        var arr = all.ToArray();
        Array.Sort(keys, arr);
        for (int i = 0; i < arr.Length; i++)
        {
            if ((i > 0 && arr[i - 1].Ticks == arr[i].Ticks) || (i + 1 < arr.Length && arr[i + 1].Ticks == arr[i].Ticks))
                arr[i].Flags |= EventFlags.SameTimestamp;
        }
        long seqBase = (dayIdx + 1L) * 100_000_000L;
        var dayStart = DateTime.ParseExact(day.ToString(), "yyyyMMdd", CultureInfo.InvariantCulture).Ticks;
        long outOfDay = arr.LongCount(e => e.Ticks < dayStart || e.Ticks >= dayStart + TimeSpan.TicksPerDay);
        _report.Add("排序與編序號", sw.Elapsed.TotalSeconds);

        // 5. index\ 分片：正規化文字（可重建）
        sw.Restart();
        using (var c = _store.OpenShard(day))
        {
            CaseStore.CreateShardSchema(c, _opt.FtsDetail, withFts: false);
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO norm(seq, ntext) VALUES($s, $t)";
            var ps = cmd.Parameters.Add("$s", SqliteType.Integer);
            var pt = cmd.Parameters.Add("$t", SqliteType.Text);
            cmd.Prepare();
            for (int i = 0; i < arr.Length; i++)
            {
                ps.Value = seqBase + i;
                pt.Value = arr[i].NormText;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        _report.Add("寫入正規化文字（分片）", sw.Elapsed.TotalSeconds);

        if (_opt.WriteFlatFile)
        {
            sw.Restart();
            FlatNormFile.Write(_store.FlatFile(day), arr.Select((e, i) => (seqBase + i, e.NormText)));
            _report.Add("寫入自訂順序檔（比較用）", sw.Elapsed.TotalSeconds);
        }

        // 6. case.db（單一寫入者）
        _caseWriter.Wait();
        try
        {
            sw.Restart();
            WriteCaseDb(day, files, hashes, dayRaw, arr, seqBase);
            _report.Add("寫入 case.db", sw.Elapsed.TotalSeconds);
        }
        finally { _caseWriter.Release(); }

        lock (_report)
        {
            _report.Events += arr.Length;
            _report.OutOfDayEvents += outOfDay;
            _report.UnparsableFiles += dayRaw.Values.Count(r => r.Unparsable);
            foreach (var e in arr)
            {
                if ((e.Flags & EventFlags.TimeBackward) != 0) _report.TimeBackward++;
                if ((e.Flags & EventFlags.SameTimestamp) != 0) _report.SameTimestamp++;
                if ((e.Flags & EventFlags.TimeInvalid) != 0) _report.TimeInvalid++;
                if ((e.Flags & EventFlags.EncodingError) != 0) _report.EncodingError++;
                if ((e.Flags & EventFlags.UnknownKind) != 0) _report.UnknownKind++;
            }
        }
        Log($"  {day}：{arr.Length:N0} 筆事件");
    }

    int ModuleRank(string module)
    {
        var i = Array.IndexOf(_opt.ModuleOrder, module);
        return i < 0 ? _opt.ModuleOrder.Length : i;
    }

    void WriteCaseDb(int day, List<LogFile> files, ConcurrentDictionary<int, string> hashes,
        ConcurrentDictionary<int, LogParser.FileResult> dayRaw, ParsedEvent[] arr, long seqBase)
    {
        using var c = _store.OpenCase();
        using var tx = c.BeginTransaction();

        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO files VALUES($id,$day,$m,$l,$p,$o,$e,$s,$h,$u)";
            foreach (var f in files)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$id", f.FileId);
                cmd.Parameters.AddWithValue("$day", day);
                cmd.Parameters.AddWithValue("$m", f.Module);
                cmd.Parameters.AddWithValue("$l", f.Logger);
                cmd.Parameters.AddWithValue("$p", f.RelativePath);
                cmd.Parameters.AddWithValue("$o", f.StreamOrder);
                cmd.Parameters.AddWithValue("$e", f.Encoding);
                cmd.Parameters.AddWithValue("$s", f.Size);
                cmd.Parameters.AddWithValue("$h", hashes[f.FileId]);
                cmd.Parameters.AddWithValue("$u", dayRaw[f.FileId].Unparsable ? 1 : 0);
                cmd.ExecuteNonQuery();
            }
        }

        var fileInfo = files.ToDictionary(f => f.FileId);
        int LoggerId(LogFile f)
        {
            if (_loggers.TryGetValue((f.Module, f.Logger), out var id)) return id;
            id = _loggers.Count + 1;
            _loggers[(f.Module, f.Logger)] = id;
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO loggers VALUES($i,$m,$n)";
            cmd.Parameters.AddWithValue("$i", id);
            cmd.Parameters.AddWithValue("$m", f.Module);
            cmd.Parameters.AddWithValue("$n", f.Logger);
            cmd.ExecuteNonQuery();
            return id;
        }
        int DictId(Dictionary<string, int> dict, string table, string idCol, string name)
        {
            if (dict.TryGetValue(name, out var id)) return id;
            id = dict.Count + 1;
            dict[name] = id;
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"INSERT INTO {table}({idCol}, name) VALUES($i,$n)";
            cmd.Parameters.AddWithValue("$i", id);
            cmd.Parameters.AddWithValue("$n", name);
            cmd.ExecuteNonQuery();
            return id;
        }

        using var ev = c.CreateCommand();
        ev.CommandText = "INSERT INTO events VALUES($seq,$ts,$f,$ls,$le,$bo,$bl,$lv,$lg,$m,$fl)";
        var p = new[] { "$seq", "$ts", "$f", "$ls", "$le", "$bo", "$bl", "$lv", "$lg", "$m", "$fl" }
            .Select(n => ev.Parameters.Add(n, SqliteType.Integer)).ToArray();
        ev.Prepare();

        using var rf = c.CreateCommand();
        rf.CommandText = "INSERT OR IGNORE INTO refs VALUES($k,$v,$s)";
        var rk = rf.Parameters.Add("$k", SqliteType.Integer);
        var rv = rf.Parameters.Add("$v", SqliteType.Integer);
        var rs = rf.Parameters.Add("$s", SqliteType.Integer);
        rf.Prepare();

        using var sc = c.CreateCommand();
        sc.CommandText = "INSERT INTO station_changes VALUES($s,$st,$su,$v)";
        var s1 = sc.Parameters.Add("$s", SqliteType.Integer);
        var s2 = sc.Parameters.Add("$st", SqliteType.Integer);
        var s3 = sc.Parameters.Add("$su", SqliteType.Integer);
        var s4 = sc.Parameters.Add("$v", SqliteType.Integer);
        sc.Prepare();

        for (int i = 0; i < arr.Length; i++)
        {
            var e = arr[i];
            long seq = seqBase + i;
            var f = fileInfo[e.FileId];
            p[0].Value = seq; p[1].Value = e.Ticks; p[2].Value = e.FileId; p[3].Value = e.LineStart; p[4].Value = e.LineEnd;
            p[5].Value = e.ByteOffset; p[6].Value = e.ByteLength; p[7].Value = (int)e.Level; p[8].Value = LoggerId(f);
            p[9].Value = ModuleRank(f.Module); p[10].Value = (int)e.Flags;
            ev.ExecuteNonQuery();

            if (e.Commands is not null)
                foreach (var v in e.Commands) { rk.Value = (int)RefKind.Command; rv.Value = v; rs.Value = seq; rf.ExecuteNonQuery(); }
            if (e.Pallets is not null)
                foreach (var v in e.Pallets) { rk.Value = (int)RefKind.Pallet; rv.Value = v; rs.Value = seq; rf.ExecuteNonQuery(); }
            if (e.Station is { } st)
            {
                s1.Value = seq;
                s2.Value = DictId(_stations, "stations", "station_id", st.Station);
                s3.Value = DictId(_statuses, "status_names", "status_id", st.Status);
                s4.Value = st.On ? 1 : 0;
                sc.ExecuteNonQuery();
            }
            e.NormText = ""; // 釋放記憶體
        }
        tx.Commit();
    }

    void Log(string s) => _opt.Log?.Invoke(s);

    /// <summary>重建 index\（ADR-0011：可重建）。由 raw\ 重新解析，得到相同的序號。</summary>
    public static ImportReport RebuildIndex(CaseStore store, string ftsDetail, bool writeFlat, Action<string>? log = null)
    {
        // 以 raw\ 當作新的 LOG 根目錄重新解析，寫到暫存案件後只搬移 index\
        var tmp = new CaseStore(store.Root + ".rebuild-tmp");
        if (Directory.Exists(tmp.Root)) Directory.Delete(tmp.Root, true);
        var rep = new Importer(tmp, new ImportOptions
        {
            DayParallelism = Environment.ProcessorCount >= 8 ? 4 : 2,
            FtsDetail = ftsDetail,
            WriteFlatFile = writeFlat,
            Log = log,
        }).Import(store.RawDir);
        CaseStore.CloseAll();
        if (Directory.Exists(store.IndexDir)) Directory.Delete(store.IndexDir, true);
        Directory.Move(tmp.IndexDir, store.IndexDir);
        Directory.Delete(tmp.Root, true);
        return rep;
    }
}
