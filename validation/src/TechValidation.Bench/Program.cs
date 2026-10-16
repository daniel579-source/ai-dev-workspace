// 技術驗證（#15）：ADR-0011 研究文件第 8 節 T-01～T-14 的量測。
// 只使用 TechValidation.DataGen 產生的虛構資料。
using System.Diagnostics;
using System.Globalization;
using System.Text;
using TechValidation.Bench;
using TechValidation.Core;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
var a = ArgMap.Parse(args);
if (!a.Has("logs") || !a.Has("work"))
{
    Console.WriteLine("""
        用法：TechValidation.Bench --logs <虛構 LOG 資料夾> --work <工作資料夾> [--report <報告.md>]
              [--reuse]          已有案件時不重新匯入
              [--skip-compare]   不跑 DuckDB／Lucene.NET 比較
              [--cold]           只跑一次查詢，標示為冷快取（請在重新開機後執行，並加上 --reuse）
              [--splits N]       單日切成幾段平行（預設 4）
        """);
    return 1;
}
var bench = new Bench(a);
bench.Run();
return 0;

sealed class ArgMap
{
    readonly Dictionary<string, string?> _m = new();
    public static ArgMap Parse(string[] args)
    {
        var m = new ArgMap();
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) continue;
            var k = args[i][2..];
            string? v = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : null;
            m._m[k] = v;
        }
        return m;
    }
    public bool Has(string k) => _m.ContainsKey(k);
    public string Get(string k, string def = "") => _m.TryGetValue(k, out var v) && v is not null ? v : def;
}

sealed class Bench
{
    readonly ArgMap _a;
    readonly string _logs, _work;
    readonly Report _r = new();
    readonly bool _reuse, _cold, _compare;
    readonly int _splits;
    readonly int _cores = Environment.ProcessorCount;

    public Bench(ArgMap a)
    {
        _a = a;
        _logs = Path.GetFullPath(a.Get("logs"));
        _work = Path.GetFullPath(a.Get("work"));
        _reuse = a.Has("reuse");
        _cold = a.Has("cold");
        _compare = !a.Has("skip-compare");
        _splits = int.Parse(a.Get("splits", "4"));
        Directory.CreateDirectory(_work);
    }

    static void Log(string s) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {s}");

    public void Run()
    {
        var reportPath = _a.Get("report", Path.Combine(_work, $"report-{DateTime.Now:yyyyMMdd-HHmm}.md"));
        _r.H1("技術驗證量測結果（#15）");
        _r.P($"產生時間：{DateTime.Now:yyyy-MM-dd HH:mm}。**所有資料都是 TechValidation.DataGen 產生的虛構資料**，不含真實 LOG。");
        if (_cold) _r.P("**本次為冷快取量測**（重新開機後第一次執行）。");

        T14_Environment();
        T13_Normalization();
        T01_Sqlite();

        var days = Importer.Discover(_logs, null).Select(f => f.Day).Distinct().Order().ToArray();
        Log($"資料天數：{days.Length}");
        var d1 = new[] { days[0] };

        var case1s = new CaseStore(Path.Combine(_work, "case-1day-serial"));
        var case1p = new CaseStore(Path.Combine(_work, "case-1day-parallel"));
        var case7 = new CaseStore(Path.Combine(_work, "case-7day"));

        if (!_cold)
        {
            _r.H2("T-03 匯入時間");
            _r.P("「基本匯入」＝讀檔、雜湊、複製到 raw\\、解析、正規化、排序與編序號、寫入 case.db 與正規化文字、events 索引。FTS5 trigram 索引另計（正式版在背景建立）。");
            var rows = new List<string[]>();
            rows.Add(ImportRow("一天，單執行緒", case1s, new ImportOptions { Days = d1, DayParallelism = 1, FileParallelism = 1, WriteFlatFile = false }));
            rows.Add(ImportRow($"一天，依檔平行（{_cores} 執行緒）", case1p, new ImportOptions { Days = d1, DayParallelism = 1, FileParallelism = _cores, WriteFlatFile = true }));
            int dp = Math.Min(days.Length, Math.Max(1, _cores / 4));
            rows.Add(ImportRow($"{days.Length} 天，依日平行（{dp} 天同時）", case7, new ImportOptions { DayParallelism = dp, FileParallelism = Math.Max(2, _cores / dp), WriteFlatFile = true }));
            _r.Table(["情境", "原始 LOG", "事件數", "基本匯入", "FTS5 建立", "快照", "合計"], rows);
            _r.P($"目標（ADR-0011）：一天份（假設 500 MB）基本匯入 **2 分鐘內**。");
            _r.Phase(_lastPhases);
            _r.P($"旗標統計（{days.Length} 天）：時間倒退 {_last7?.TimeBackward:N0}、同時間戳記 {_last7?.SameTimestamp:N0}、時間無效 {_last7?.TimeInvalid:N0}、編碼錯誤 {_last7?.EncodingError:N0}、超出日期資料夾的事件 {_last7?.OutOfDayEvents:N0}、無法解析的檔案 {_last7?.UnparsableFiles}。");

            T04_IndexSize(case7, days[0]);
            Sizes(case7);
        }

        DuckDbBackend? duck = null; LuceneBackend? lucene = null;
        if (_compare)
        {
            duck = new DuckDbBackend(Path.Combine(_work, "compare.duckdb"));
            lucene = new LuceneBackend(Path.Combine(_work, "compare-lucene"));
            if (!_cold && !(_reuse && File.Exists(duck.Path) && Directory.Exists(lucene.Path)))
            {
                _r.H2("比較對象的載入時間與大小");
                Log("DuckDB 載入…");
                var td = duck.Load(case7, days);
                Log("Lucene.NET 建索引…");
                var tl = lucene.Load(case7, days);
                _r.Table(["方案", "載入／建索引", "大小", "與原始 LOG 比"],
                [
                    ["DuckDB（正規化文字）", Sec(td), Mb(new FileInfo(duck.Path).Length), Ratio(new FileInfo(duck.Path).Length, _rawBytes7)],
                    ["Lucene.NET 3-gram", Sec(tl), Mb(DirSize(lucene.Path)), Ratio(DirSize(lucene.Path), _rawBytes7)],
                ]);
            }
        }

        Queries(case7, days, duck, lucene);
        if (_cold) { Write(reportPath); return; }
        T08_Regex(case7, days, duck);
        T09_Refs(case7);
        T10_Snapshots(case7);
        T11_Order(case7);
        T12_Portability(case1p, days[0]);
        _r.H2("T-14 目標硬體");
        _r.P("上面的「執行環境」是**開發用電腦**。工程師電腦的磁碟類型與記憶體**待確認**（ADR-0011）；若工程師電腦是硬碟或記憶體較小，需在該電腦重跑本程式。");
        Write(reportPath);
    }

    void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, _r.ToString(), new UTF8Encoding(false));
        Log($"報告：{path}");
    }

    // ---------------- T-14 / T-13 / T-01 ----------------

    void T14_Environment()
    {
        _r.H2("執行環境（T-14）");
        var gc = GC.GetGCMemoryInfo();
        string cpu = "";
        try { if (OperatingSystem.IsWindows()) cpu = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", "")?.ToString()?.Trim() ?? ""; } catch { }
        string disk = Shell("powershell", "-NoProfile -Command \"Get-PhysicalDisk | ForEach-Object { $_.FriendlyName + ' / ' + $_.MediaType + ' / ' + $_.BusType }\"");
        _r.Table(["項目", "值"],
        [
            ["OS", Environment.OSVersion.VersionString],
            [".NET", Environment.Version.ToString()],
            ["CPU", cpu],
            ["邏輯處理器", _cores.ToString()],
            ["記憶體", $"{gc.TotalAvailableMemoryBytes / 1073741824.0:F1} GB"],
            ["磁碟", disk.Replace("\r", "").Replace("\n", "<br>")],
            ["工作資料夾", _work],
        ]);
    }

    static string Shell(string exe, string args)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8 })!;
            var o = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return o.Trim();
        }
        catch (Exception ex) { return ex.Message; }
    }

    void T13_Normalization()
    {
        _r.H2("T-13 NFKC＋大小寫摺疊");
        var rows = NormalizationCases.All.Select(c =>
        {
            var actual = TextNormalizer.Normalize(c.Input);
            return new[] { c.Name, Show(c.Input), Show(actual), Show(c.Expected), actual == c.Expected ? "通過" : "**不符**" };
        }).ToList();
        _r.Table(["案例", "輸入", "結果", "預期", "判定"], rows);
        _r.P("「不同 Windows 版本的結果一致」需在其他版本的 Windows 上執行同一程式比對（見 README 的手動檢查）。");
        static string Show(string s) => "`" + s.Replace("|", "\\|") + "`";
    }

    void T01_Sqlite()
    {
        _r.H2("T-01 SQLite 與 FTS5 trigram");
        var ok = CaseStore.TrigramAvailable(out var detail);
        _r.P($"本程式（framework-dependent）：{detail} → {(ok ? "可用" : "**不可用**")}。self-contained 發佈與乾淨、離線的 Windows 10 由 TechValidation.UiSpike 的自我檢查驗證。");
    }

    // ---------------- T-03 / T-04 ----------------

    Dictionary<string, double> _lastPhases = new();
    ImportReport? _last7;
    long _rawBytes7;

    string[] ImportRow(string name, CaseStore store, ImportOptions opt)
    {
        ImportReport rep;
        if (_reuse && File.Exists(store.CaseDb))
        {
            Log($"沿用 {store.Root}");
            rep = new ImportReport();
            using var c = store.OpenCase(true);
            rep.Events = CaseStore.Scalar<long>(c, "SELECT count(*) FROM events");
            rep.RawBytes = CaseStore.Scalar<long>(c, "SELECT sum(size) FROM files");
            rep.BasicImportSeconds = rep.FtsSeconds = rep.SnapshotSeconds = rep.TotalSeconds = double.NaN;
        }
        else
        {
            if (Directory.Exists(store.Root)) Directory.Delete(store.Root, true);
            Log($"匯入：{name}");
            rep = new Importer(store, new ImportOptions
            {
                Days = opt.Days, DayParallelism = opt.DayParallelism, FileParallelism = opt.FileParallelism,
                WriteFlatFile = opt.WriteFlatFile, Log = Log,
            }).Import(_logs);
            _lastPhases = rep.PhaseSeconds.ToDictionary();
        }
        CaseStore.CloseAll();
        if (store.Root.EndsWith("case-7day")) { _last7 = rep; _rawBytes7 = rep.RawBytes; }
        return [name, Mb(rep.RawBytes), rep.Events.ToString("N0"), Sec(rep.BasicImportSeconds), Sec(rep.FtsSeconds), Sec(rep.SnapshotSeconds), Sec(rep.TotalSeconds)];
    }

    void T04_IndexSize(CaseStore store, int day)
    {
        _r.H2("T-04 trigram 索引大小（一天份）");
        Log("T-04：比較 detail=full／column／none");
        var rows = new List<string[]>();
        var dir = Path.Combine(_work, "t04");
        Directory.CreateDirectory(dir);
        long normOnly = 0;
        foreach (var detail in new[] { "full", "column", "none" })
        {
            var path = Path.Combine(dir, $"{day}-{detail}.db");
            if (File.Exists(path)) File.Delete(path);
            using (var c = CaseStore.Open(path))
            {
                CaseStore.Exec(c, "PRAGMA journal_mode=DELETE;");
                CaseStore.Exec(c, $"ATTACH DATABASE '{store.ShardDb(day)}' AS s; CREATE TABLE norm(seq INTEGER PRIMARY KEY, ntext TEXT NOT NULL); INSERT INTO norm SELECT * FROM s.norm; DETACH DATABASE s;");
            }
            CaseStore.CloseAll();
            normOnly = new FileInfo(path).Length;
            var sw = Stopwatch.StartNew();
            using (var c = CaseStore.Open(path))
            {
                CaseStore.CreateFts(c, detail);
                CaseStore.Exec(c, "INSERT INTO fts(fts) VALUES('rebuild');");
            }
            var t = sw.Elapsed.TotalSeconds;
            CaseStore.CloseAll();
            long size = new FileInfo(path).Length;
            rows.Add([detail, Sec(t), Mb(size - normOnly), $"{(size - normOnly) / (double)normOnly:F2} 倍"]);
        }
        _r.Table(["detail", "建立時間", "FTS 索引大小", "相對於正規化文字"], rows);
        _r.P($"正規化文字（norm 表）：{Mb(normOnly)}。detail=column／none 不支援片語查詢，長於 3 字的詞要改為「所有 trigram 都出現」再以 instr 驗證，查詢時間見 T-05。");
    }

    void Sizes(CaseStore s)
    {
        _r.H2("案件磁碟用量（7 天）");
        long raw = s.DirectorySize(s.RawDir);
        long caseDb = new FileInfo(s.CaseDb).Length;
        long shards = Directory.GetFiles(s.IndexDir, "*.db").Sum(f => new FileInfo(f).Length);
        long flat = Directory.GetFiles(s.IndexDir, "*.norm*").Sum(f => new FileInfo(f).Length);
        long total = raw + caseDb + shards;
        _r.Table(["部分", "大小", "與原始 LOG 比"],
        [
            ["raw\\（原始檔複本）", Mb(raw), Ratio(raw, _rawBytes7)],
            ["case.db（事件、參照、快照）", Mb(caseDb), Ratio(caseDb, _rawBytes7)],
            ["index\\ 分片（正規化文字＋FTS5 full）", Mb(shards), Ratio(shards, _rawBytes7)],
            ["**合計（不含比較用檔案）**", Mb(total), Ratio(total, _rawBytes7)],
            ["（比較用）自訂順序檔", Mb(flat), Ratio(flat, _rawBytes7)],
        ]);
        _r.P("上限（ADR-0011）：原始 LOG 的 **5 倍**。");
    }

    // ---------------- T-05 / T-06 / T-07 / T-11 ----------------

    sealed record QDef(string Id, string Desc, SearchQuery Q, string Item);

    static readonly QDef[] QueryDefs =
    [
        new("Q1", "罕見英數（≥3 字）", new SearchQuery { All = ["ZQX7781"] }, "T-05"),
        new("Q2", "常見英數（≥3 字）", new SearchQuery { All = ["Station_Status_Change"] }, "T-05"),
        new("Q3", "罕見中文（4 字）", new SearchQuery { All = ["急停按鈕"] }, "T-05"),
        new("Q4", "全形／半形混用", new SearchQuery { All = ["ERR-042"] }, "T-05/T-13"),
        new("Q5", "半形片假名", new SearchQuery { All = ["アラーム"] }, "T-05/T-13"),
        new("S1", "罕見中文 2 字", new SearchQuery { All = ["斷線"] }, "T-06"),
        new("S2", "常見中文 2 字", new SearchQuery { All = ["在席"] }, "T-06"),
        new("S3", "1 字", new SearchQuery { All = ["鎖"] }, "T-06"),
        new("C1", "AND＋OR（含 2 字）＋NOT", new SearchQuery { All = ["station_status_change"], Any = ["在席", "取物可"], Not = ["= off"] }, "T-07"),
        new("C2", "OR（≥3 字）＋NOT", new SearchQuery { Any = ["急停按鈕", "zqx7781", "條碼讀取失敗"], Not = ["a-01 "] }, "T-07"),
        new("C3", "只有不包含", new SearchQuery { Not = ["station"] }, "T-07"),
    ];

    readonly Dictionary<string, List<long>> _baseline = new();

    void Queries(CaseStore store, int[] days, DuckDbBackend? duck, LuceneBackend? lucene)
    {
        var sqlite = new SqliteSearch(store) { SplitsPerDay = _splits, FtsDetail = "full" };
        var flat = new FlatFileSearch(store) { SplitsPerDay = _splits };
        int runs = _cold ? 1 : 3;
        var mismatches = new List<string>();

        // 命令編號篩選用：取一個出現次數中等的命令
        long cmdNo;
        using (var c = store.OpenCase(true))
            cmdNo = CaseStore.Scalar<long>(c, "SELECT value FROM refs WHERE kind=1 AND seq < 200000000 GROUP BY value ORDER BY count(*) DESC, value LIMIT 1 OFFSET (SELECT count(DISTINCT value) / 2 FROM refs WHERE kind=1 AND seq < 200000000)");
        var filterDefs = new[]
        {
            new QDef("F1", "關鍵字＋模組＋等級", new SearchQuery { All = ["cmd_no"], Modules = [1], Levels = [0] }, "T-07"),
            new QDef("F2", "關鍵字＋命令編號", new SearchQuery { All = ["station"], CommandNo = cmdNo }, "T-07"),
            new QDef("F3", "2 字＋命令編號", new SearchQuery { All = ["在席", "命令"], CommandNo = cmdNo }, "T-07"),
        };

        foreach (var (label, dayset) in new[] { ("一天", new[] { days[0] }), ($"{days.Length} 天", days) })
        {
            _r.H2($"T-05／T-06／T-07 搜尋時間（{label}）");
            _r.P(_cold ? "冷快取：每個查詢只跑一次。" : $"每個查詢跑 {runs} 次：「首次」為第 1 次，「熱快取」為之後幾次的中位數。時間單位：毫秒。「第一筆」＝依序號順序可以開始顯示結果的時間。單日切 {_splits} 段平行，{_cores} 執行緒。");
            var rows = new List<string[]>();
            foreach (var qd in QueryDefs.Concat(filterDefs))
            {
                var engines = new List<(string, Func<SearchResult>)>();
                if (qd.Q.Normalized().CanUseIndex) engines.Add(("fts", () => sqlite.Search(qd.Q, dayset, useIndex: true)));
                engines.Add(("scan", () => sqlite.Search(qd.Q, dayset, useIndex: false)));
                if (!qd.Q.HasFilters)
                {
                    engines.Add(("flat", () => flat.Search(qd.Q, dayset)));
                    if (duck is not null) engines.Add(("duck", () => duck.Search(qd.Q, dayset)));
                    if (lucene is not null) engines.Add(("lucene", () => lucene.Search(qd.Q, dayset)));
                }
                foreach (var (_, f) in engines)
                {
                    var results = new List<SearchResult>();
                    for (int i = 0; i < runs; i++) results.Add(f());
                    var first = results[0];
                    if (first.Note is not null)
                    {
                        rows.Add([qd.Id, qd.Desc, first.Engine, "—", "—", "—", first.Note]);
                        continue;
                    }
                    var key = $"{qd.Id}/{label}";
                    // T-11：同一查詢重複執行、不同方案的結果必須完全相同
                    foreach (var r in results.Skip(1))
                        if (!r.Seqs.SequenceEqual(first.Seqs)) mismatches.Add($"{key} {first.Engine} 重複執行結果不同");
                    if (_baseline.TryGetValue(key, out var b))
                    {
                        if (!b.SequenceEqual(first.Seqs)) mismatches.Add($"{key} {first.Engine} 與基準結果不同（{first.Count} vs {b.Count}）");
                    }
                    else _baseline[key] = first.Seqs;
                    if (!IsSorted(first.Seqs)) mismatches.Add($"{key} {first.Engine} 結果未依序號排序");

                    var warm = results.Count > 1 ? results.Skip(1).ToList() : results;
                    rows.Add([qd.Id, qd.Desc, first.Engine, first.Count.ToString("N0"),
                        $"{Ms(first.FirstResultMs)} / {Ms(first.TotalMs)}",
                        $"{Ms(Median(warm.Select(x => x.FirstResultMs)))} / {Ms(Median(warm.Select(x => x.TotalMs)))}", ""]);
                    Log($"{key} {first.Engine}: {first.Count} 筆, {first.TotalMs:F0} ms");
                }
            }
            _r.Table(["查詢", "說明", "方案", "筆數", "首次 第一筆／全部", "熱快取 第一筆／全部", "備註"], rows);
        }
        _r.P("查詢定義：" + string.Join("；", QueryDefs.Concat(filterDefs).Select(q => $"{q.Id} {q.Q}")));
        _r.P("目標（ADR-0010）：一般關鍵字一天份 **2 秒**、七天份 **10 秒**內開始顯示結果。");
        _r.H2("T-11 結果一致性（搜尋）");
        _r.P(mismatches.Count == 0
            ? "所有查詢：重複執行結果相同、各方案結果相同、結果依序號排序 → **通過**。"
            : "**不通過**：\n\n" + string.Join("\n", mismatches.Select(m => "- " + m)));
    }

    static bool IsSorted(List<long> l) { for (int i = 1; i < l.Count; i++) if (l[i] < l[i - 1]) return false; return true; }

    // ---------------- T-08 ----------------

    void T08_Regex(CaseStore store, int[] days, DuckDbBackend? duck)
    {
        _r.H2("T-08 正規表示式掃描");
        var sqlite = new SqliteSearch(store) { SplitsPerDay = _splits };
        var flat = new FlatFileSearch(store) { SplitsPerDay = _splits };
        var patterns = new[] { @"err-?0[0-9]2", @"cmd_no=000[0-9]{5}.*a-0[1-5]" };
        var rows = new List<string[]>();
        foreach (var (label, dayset) in new[] { ("一天", new[] { days[0] }), ($"{days.Length} 天", days) })
        foreach (var p in patterns)
        foreach (var nb in new[] { false, true })
        {
            var q = new SearchQuery { Regex = p, RegexNonBacktracking = nb };
            foreach (var r in new[] { sqlite.Search(q, dayset, false), flat.Search(q, dayset) })
                rows.Add([label, $"`{p}`", r.Engine, r.Count.ToString("N0"), Ms(r.FirstResultMs), Ms(r.TotalMs)]);
            Log($"regex {label} {p} nb={nb}");
        }
        if (duck is not null)
            foreach (var p in patterns)
            {
                var r = duck.Search(new SearchQuery { Regex = p }, days);
                rows.Add([$"{days.Length} 天", $"`{p}`", r.Engine, r.Count.ToString("N0"), Ms(r.FirstResultMs), Ms(r.TotalMs)]);
            }
        _r.Table(["範圍", "樣式", "方案", "筆數", "第一筆 ms", "全部 ms"], rows);

        // 取消的反應時間
        var cancelRows = new List<string[]>();
        foreach (var engine in new[] { "sqlite", "flat" })
        {
            using var cts = new CancellationTokenSource();
            var q = new SearchQuery { Regex = @"(a|b|c|d|e)+[0-9]{3}x" };
            var sw = Stopwatch.StartNew();
            var task = Task.Run(() => engine == "sqlite" ? sqlite.Search(q, days, false, cts.Token) : flat.Search(q, days, cts.Token));
            Thread.Sleep(500);
            var tCancel = sw.Elapsed.TotalMilliseconds;
            cts.Cancel();
            var res = task.Result;
            cancelRows.Add([res.Engine, res.Cancelled ? "已取消" : "取消前已完成", Ms(sw.Elapsed.TotalMilliseconds - tCancel)]);
        }
        _r.Table(["方案", "結果", "按下取消到停止 ms"], cancelRows);
        _r.P("正規表示式不設效能目標（ADR-0010），只記錄供參考。DuckDB 使用 RE2 語法，與 .NET 不同。");
    }

    // ---------------- T-09 ----------------

    void T09_Refs(CaseStore store)
    {
        _r.H2("T-09 以命令編號、棧板編號串連事件（7 天）");
        using var c = store.OpenCase(true);
        var rows = new List<string[]>();
        foreach (var (kind, name) in new[] { (RefKind.Command, "命令編號"), (RefKind.Pallet, "棧板編號") })
        {
            var values = new List<long>();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = $"SELECT value FROM refs WHERE kind={(int)kind} GROUP BY value ORDER BY abs(random()) LIMIT 30";
                using var r = cmd.ExecuteReader();
                while (r.Read()) values.Add(r.GetInt64(0));
            }
            var times = new List<double>();
            long totalRows = 0;
            foreach (var v in values)
            {
                var sw = Stopwatch.StartNew();
                using var cmd = c.CreateCommand();
                cmd.CommandText = $"SELECT e.seq, e.ts, e.file_id, e.line_start, e.flags FROM refs r JOIN events e ON e.seq = r.seq WHERE r.kind={(int)kind} AND r.value=$v ORDER BY r.seq";
                cmd.Parameters.AddWithValue("$v", v);
                using var rd = cmd.ExecuteReader();
                while (rd.Read()) totalRows++;
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            rows.Add([name, values.Count.ToString(), (totalRows / Math.Max(1, values.Count)).ToString("N0"), Ms(times.Average()), Ms(times[(int)(times.Count * 0.95)]), Ms(times[^1])]);
        }
        _r.Table(["種類", "抽樣數", "平均事件數", "平均 ms", "p95 ms", "最大 ms"], rows);
        _r.P("建議目標（研究文件，**假設**）：< 500 ms。");
    }

    // ---------------- T-10 ----------------

    void T10_Snapshots(CaseStore store)
    {
        _r.H2("T-10 狀態快照");
        using var c = store.OpenCase(true);
        long count = CaseStore.Scalar<long>(c, "SELECT count(*) FROM snapshots");
        long bytes = CaseStore.Scalar<long>(c, "SELECT coalesce(sum(length(data)),0) FROM snapshots");
        long changes = CaseStore.Scalar<long>(c, "SELECT count(*) FROM station_changes");
        long lo = CaseStore.Scalar<long>(c, "SELECT min(seq) FROM events"), hi = CaseStore.Scalar<long>(c, "SELECT max(seq) FROM events");
        var seqs = new List<long>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT seq FROM events ORDER BY abs(random()) LIMIT 200";
            using var r = cmd.ExecuteReader();
            while (r.Read()) seqs.Add(r.GetInt64(0));
        }
        var times = new List<double>();
        foreach (var s in seqs)
        {
            var sw = Stopwatch.StartNew();
            Snapshots.StateAt(c, s);
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        int ok = 0, checkedN = 0;
        double fullMs = 0;
        foreach (var s in seqs.Take(10))
        {
            var sw = Stopwatch.StartNew();
            var full = Snapshots.StateAtFull(c, s);
            fullMs += sw.Elapsed.TotalMilliseconds;
            checkedN++;
            if (full.Fingerprint() == Snapshots.StateAt(c, s).Fingerprint()) ok++;
        }
        _r.Table(["項目", "值"],
        [
            ["站點狀態變化事件", changes.ToString("N0")],
            ["快照數（每 20,000 筆事件一份）", count.ToString("N0")],
            ["快照總大小", $"{bytes / 1024.0:F0} KB"],
            ["快照建立時間", Sec(_last7?.SnapshotSeconds ?? double.NaN)],
            ["從快照推導任一時刻：平均／p95／最大", $"{Ms(times.Average())} / {Ms(times[(int)(times.Count * 0.95)])} / {Ms(times[^1])} ms"],
            ["從頭推導（對照）：平均", $"{Ms(fullMs / checkedN)} ms"],
            ["快照推導與從頭推導一致", $"{ok}/{checkedN}"],
        ]);
        _r.P("簡化：只追蹤站點的 ON／OFF 位元狀態；完整的世界模型（#24）狀態更多，快照會更大。");
    }

    // ---------------- T-11 ----------------

    void T11_Order(CaseStore store)
    {
        _r.H2("T-11 穩定排序（事件表）");
        using var c = store.OpenCase(true);
        long back = CaseStore.Scalar<long>(c, "SELECT count(*) FROM (SELECT ts, lag(ts) OVER (ORDER BY seq) AS p FROM events) WHERE ts < p");
        _r.P($"依事件序號讀取時，標準化時間倒退的筆數：{back} → {(back == 0 ? "**通過**（時間軸單調）" : "**不通過**")}。搜尋結果的一致性見上方 T-11。");
    }

    // ---------------- T-12 ----------------

    void T12_Portability(CaseStore src, int day)
    {
        _r.H2("T-12 可攜性與重建 index\\");
        CaseStore.CloseAll();
        var wal = Directory.GetFiles(src.Root, "*-wal", SearchOption.AllDirectories).Where(f => new FileInfo(f).Length > 0).ToList();
        var dst = new CaseStore(Path.Combine(_work, "case-1day-copy"));
        if (Directory.Exists(dst.Root)) Directory.Delete(dst.Root, true);
        CopyDir(src.Root, dst.Root);

        var probe = new[] { QueryDefs[1], QueryDefs[6], QueryDefs[8] };
        string Sig(CaseStore s)
        {
            var sq = new SqliteSearch(s) { SplitsPerDay = 2 };
            return string.Join("|", probe.Select(q => string.Join(",", sq.Search(q.Q, [day], false).Seqs).GetHashCode()))
                 + "|" + string.Join("|", probe.Where(q => q.Q.Normalized().CanUseIndex).Select(q => string.Join(",", sq.Search(q.Q, [day], true).Seqs).GetHashCode()));
        }
        var a = Sig(src);
        var b = Sig(dst);
        CaseStore.CloseAll();
        Log("刪除 index\\ 後重建…");
        Directory.Delete(dst.IndexDir, true);
        var sw = Stopwatch.StartNew();
        Importer.RebuildIndex(dst, "full", writeFlat: false, Log);
        var tRebuild = sw.Elapsed.TotalSeconds;
        var c2 = Sig(dst);
        CaseStore.CloseAll();
        _r.Table(["檢查", "結果"],
        [
            ["關閉後留下非空的 -wal 檔", wal.Count == 0 ? "無 → **通過**" : "**有**：" + string.Join(", ", wal.Select(Path.GetFileName))],
            ["複製到另一個資料夾後開啟，結果相同", a == b ? "**通過**" : "**不通過**"],
            ["刪除 index\\ 後重建，結果相同", a == c2 ? $"**通過**（重建 {Sec(tRebuild)}）" : "**不通過**"],
        ]);
        _r.P("「複製到另一台電腦」需手動檢查（見 README）。");
    }

    static void CopyDir(string from, string to)
    {
        foreach (var d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(d.Replace(from, to));
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories)) File.Copy(f, f.Replace(from, to), true);
    }

    // ---------------- helpers ----------------

    static long DirSize(string d) => new DirectoryInfo(d).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
    static string Mb(long b) => $"{b / 1048576.0:N0} MB";
    static string Ratio(long a, long b) => b == 0 ? "—" : $"{a / (double)b:F2} 倍";
    static string Sec(double s) => double.IsNaN(s) ? "—" : $"{s:F1} 秒";
    static string Ms(double ms) => double.IsNaN(ms) ? "—" : ms < 10 ? $"{ms:F1}" : $"{ms:F0}";
    static double Median(IEnumerable<double> xs)
    {
        var l = xs.Where(x => !double.IsNaN(x)).Order().ToList();
        return l.Count == 0 ? double.NaN : l[l.Count / 2];
    }
}

sealed class Report
{
    readonly StringBuilder _sb = new();
    public void H1(string s) => _sb.AppendLine($"# {s}").AppendLine();
    public void H2(string s) => _sb.AppendLine($"## {s}").AppendLine();
    public void P(string s) => _sb.AppendLine(s).AppendLine();
    public void Table(string[] head, IEnumerable<string[]> rows)
    {
        _sb.AppendLine("| " + string.Join(" | ", head) + " |");
        _sb.AppendLine("|" + string.Concat(head.Select(_ => "---|")));
        foreach (var r in rows) _sb.AppendLine("| " + string.Join(" | ", r) + " |");
        _sb.AppendLine();
    }
    public void Phase(Dictionary<string, double> phases)
    {
        if (phases.Count == 0) return;
        P("最後一次匯入（7 天）的各階段：" + string.Join("；", phases.Select(kv => $"{kv.Key} {kv.Value:F1} 秒")) + "。各執行緒合計的時間會大於實際經過時間。");
    }
    public override string ToString() => _sb.ToString();
}
