using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace TechValidation.Core;

/// <summary>搜尋條件（ADR-0010）：三個輸入框＋篩選。字串在建構時正規化。</summary>
public sealed class SearchQuery
{
    public string[] All { get; init; } = [];
    public string[] Any { get; init; } = [];
    public string[] Not { get; init; } = [];
    public int[]? Modules { get; init; }
    public int[]? Levels { get; init; }
    public long? CommandNo { get; init; }
    public string? Regex { get; init; }
    public bool RegexNonBacktracking { get; init; }

    public SearchQuery Normalized() => new()
    {
        All = All.Select(TextNormalizer.Normalize).ToArray(),
        Any = Any.Select(TextNormalizer.Normalize).ToArray(),
        Not = Not.Select(TextNormalizer.Normalize).ToArray(),
        Modules = Modules, Levels = Levels, CommandNo = CommandNo,
        Regex = Regex, RegexNonBacktracking = RegexNonBacktracking,
    };

    public bool HasFilters => Modules is not null || Levels is not null || CommandNo is not null;

    /// <summary>ADR-0011：所有正向條件都至少 3 個字時，才能用 trigram 索引縮小範圍。</summary>
    public bool CanUseIndex =>
        Regex is null &&
        (All.Any(t => t.Length >= 3) || (Any.Length > 0 && Any.All(t => t.Length >= 3)));

    public override string ToString()
    {
        var parts = new List<string>();
        if (All.Length > 0) parts.Add("全部包含[" + string.Join(" ", All) + "]");
        if (Any.Length > 0) parts.Add("任一包含[" + string.Join(" ", Any) + "]");
        if (Not.Length > 0) parts.Add("不包含[" + string.Join(" ", Not) + "]");
        if (Regex is not null) parts.Add("regex[" + Regex + (RegexNonBacktracking ? ", NonBacktracking" : "") + "]");
        if (Modules is not null) parts.Add("模組=" + string.Join(",", Modules));
        if (Levels is not null) parts.Add("等級=" + string.Join(",", Levels));
        if (CommandNo is not null) parts.Add("命令=" + CommandNo);
        return string.Join(" ", parts);
    }

    public bool MatchText(string norm)
    {
        foreach (var t in All) if (!norm.Contains(t, StringComparison.Ordinal)) return false;
        if (Any.Length > 0 && !Any.Any(t => norm.Contains(t, StringComparison.Ordinal))) return false;
        foreach (var t in Not) if (norm.Contains(t, StringComparison.Ordinal)) return false;
        return true;
    }
}

/// <summary>搜尋的量測結果。</summary>
public sealed class SearchResult
{
    public required string Engine { get; init; }
    public List<long> Seqs { get; } = new();
    public double FirstResultMs { get; set; } = double.NaN;
    public double TotalMs { get; set; }
    public bool Cancelled { get; set; }
    public string? Note { get; set; }
    public long Count => Seqs.Count;
}

/// <summary>
/// 依序完成的分段執行：分段可以平行跑，但結果依分段順序（＝事件序號順序）交出。
/// 「第一筆結果時間」＝第一個有結果的分段，以及它之前所有分段都完成的時間。
/// </summary>
public static class OrderedPartitions
{
    public static SearchResult Run(string engine, int parts, Func<int, CancellationToken, List<long>> work, int parallelism, CancellationToken ct = default)
    {
        var res = new SearchResult { Engine = engine };
        var sw = Stopwatch.StartNew();
        var results = new List<long>?[parts];
        var doneAt = new double[parts];
        try
        {
            Parallel.For(0, parts, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct }, i =>
            {
                results[i] = work(i, ct);
                doneAt[i] = sw.Elapsed.TotalMilliseconds;
            });
        }
        catch (OperationCanceledException)
        {
            res.Cancelled = true;
        }
        res.TotalMs = sw.Elapsed.TotalMilliseconds;
        double maxDone = 0;
        for (int i = 0; i < parts; i++)
        {
            if (results[i] is null) break;
            maxDone = Math.Max(maxDone, doneAt[i]);
            if (results[i]!.Count > 0 && double.IsNaN(res.FirstResultMs)) res.FirstResultMs = maxDone;
            res.Seqs.AddRange(results[i]!);
        }
        return res;
    }
}

/// <summary>SQLite 路徑：FTS5 trigram（≥3 字）或在 SQLite 內以 instr 掃描正規化文字。</summary>
public sealed class SqliteSearch
{
    readonly CaseStore _store;
    public int Parallelism { get; set; } = Environment.ProcessorCount;
    /// <summary>單日內再依序號範圍切幾段（讓一天份也能平行）。</summary>
    public int SplitsPerDay { get; set; } = 1;
    public string FtsDetail { get; set; } = "full";

    public SqliteSearch(CaseStore store) => _store = store;

    public SearchResult Search(SearchQuery q, IReadOnlyList<int> days, bool useIndex, CancellationToken ct = default)
    {
        q = q.Normalized();
        if (useIndex && !q.CanUseIndex) throw new InvalidOperationException("此查詢不能使用索引");
        var ranges = DayRanges(days);
        var engine = useIndex ? $"SQLite FTS5 trigram (detail={FtsDetail})" : "SQLite 掃描 (instr)";
        Regex? rx = q.Regex is null ? null : new Regex(q.Regex, q.RegexNonBacktracking ? RegexOptions.NonBacktracking : RegexOptions.None);
        if (rx is not null) engine = "SQLite 讀取＋.NET Regex" + (q.RegexNonBacktracking ? " (NonBacktracking)" : "");

        return OrderedPartitions.Run(engine, ranges.Count, (i, token) =>
        {
            var (day, lo, hi) = ranges[i];
            using var c = _store.OpenShard(day, readOnly: true);
            if (q.HasFilters) Attach(c);
            using var cmd = c.CreateCommand();
            var where = new List<string> { "n.seq BETWEEN $lo AND $hi" };
            cmd.Parameters.AddWithValue("$lo", lo);
            cmd.Parameters.AddWithValue("$hi", hi);
            string from = "norm n";
            if (rx is null)
            {
                if (useIndex)
                {
                    from = "fts JOIN norm n ON n.seq = fts.rowid";
                    where.Add("fts MATCH $m");
                    cmd.Parameters.AddWithValue("$m", FtsExpression(q, FtsDetail));
                }
                AddInstr(cmd, where, q, verifyAll: !useIndex || FtsDetail != "full");
            }
            AddFilters(cmd, where, q);
            cmd.CommandText = $"SELECT n.seq{(rx is null ? "" : ", n.ntext")} FROM {from}{(q.HasFilters ? " JOIN c.events e ON e.seq = n.seq" : "")} WHERE {string.Join(" AND ", where)} ORDER BY n.seq";
            var list = new List<long>();
            using var r = cmd.ExecuteReader();
            int k = 0;
            while (r.Read())
            {
                if ((++k & 1023) == 0) token.ThrowIfCancellationRequested();
                if (rx is not null && !rx.IsMatch(r.GetString(1))) continue;
                list.Add(r.GetInt64(0));
            }
            return list;
        }, Parallelism, ct);
    }

    void Attach(SqliteConnection c)
    {
        // 連線集區會保留先前 ATTACH 的資料庫
        if (CaseStore.Scalar<long>(c, "SELECT count(*) FROM pragma_database_list WHERE name = 'c'") > 0) return;
        using var cmd = c.CreateCommand();
        cmd.CommandText = "ATTACH DATABASE $p AS c";
        cmd.Parameters.AddWithValue("$p", _store.CaseDb);
        cmd.ExecuteNonQuery();
    }

    List<(int Day, long Lo, long Hi)> DayRanges(IReadOnlyList<int> days)
    {
        var list = new List<(int, long, long)>();
        foreach (var d in days)
        {
            using var c = _store.OpenShard(d, readOnly: true);
            long lo = CaseStore.Scalar<long>(c, "SELECT min(seq) FROM norm");
            long hi = CaseStore.Scalar<long>(c, "SELECT max(seq) FROM norm");
            for (int s = 0; s < SplitsPerDay; s++)
            {
                long a = lo + (hi - lo + 1) * s / SplitsPerDay;
                long b = lo + (hi - lo + 1) * (s + 1) / SplitsPerDay - 1;
                list.Add((d, a, b));
            }
        }
        return list;
    }

    static string Quote(string t) => "\"" + t.Replace("\"", "\"\"") + "\"";

    /// <summary>
    /// FTS5 查詢式。detail=full 時，trigram 對整個詞做片語比對（精確）；
    /// detail=column／none 不支援片語，改為「所有 trigram 都出現」再以 instr 驗證。
    /// </summary>
    public static string FtsExpression(SearchQuery q, string detail)
    {
        string Term(string t) => detail == "full" || t.Length == 3
            ? Quote(t)
            : "(" + string.Join(" AND ", Enumerable.Range(0, t.Length - 2).Select(i => t.Substring(i, 3)).Distinct().Select(Quote)) + ")";
        var parts = new List<string>();
        foreach (var t in q.All.Where(t => t.Length >= 3)) parts.Add(Term(t));
        if (q.Any.Length > 0 && q.Any.All(t => t.Length >= 3)) parts.Add("(" + string.Join(" OR ", q.Any.Select(Term)) + ")");
        var expr = string.Join(" AND ", parts);
        if (detail == "full")
            foreach (var t in q.Not.Where(t => t.Length >= 3)) expr += " NOT " + Term(t);
        return expr;
    }

    static void AddInstr(SqliteCommand cmd, List<string> where, SearchQuery q, bool verifyAll)
    {
        int n = 0;
        string P(string v) { var name = "$t" + n++; cmd.Parameters.AddWithValue(name, v); return name; }
        foreach (var t in q.All) if (verifyAll || t.Length < 3) where.Add($"instr(n.ntext, {P(t)}) > 0");
        if (q.Any.Length > 0 && (verifyAll || q.Any.Any(t => t.Length < 3)))
            where.Add("(" + string.Join(" OR ", q.Any.Select(t => $"instr(n.ntext, {P(t)}) > 0")) + ")");
        foreach (var t in q.Not) if (verifyAll || t.Length < 3) where.Add($"instr(n.ntext, {P(t)}) = 0");
    }

    static void AddFilters(SqliteCommand cmd, List<string> where, SearchQuery q)
    {
        if (q.Modules is not null) where.Add($"e.module_id IN ({string.Join(",", q.Modules)})");
        if (q.Levels is not null) where.Add($"e.level IN ({string.Join(",", q.Levels)})");
        if (q.CommandNo is not null)
        {
            where.Add("n.seq IN (SELECT seq FROM c.refs WHERE kind = 1 AND value = $cmd)");
            cmd.Parameters.AddWithValue("$cmd", q.CommandNo.Value);
        }
    }
}

/// <summary>自訂順序檔的掃描（研究文件方案 E 的掃描部分）。只支援文字條件，不支援篩選。</summary>
public sealed class FlatFileSearch
{
    readonly CaseStore _store;
    public int Parallelism { get; set; } = Environment.ProcessorCount;
    public int SplitsPerDay { get; set; } = 1;

    public FlatFileSearch(CaseStore store) => _store = store;

    public SearchResult Search(SearchQuery q, IReadOnlyList<int> days, CancellationToken ct = default)
    {
        q = q.Normalized();
        if (q.HasFilters) throw new NotSupportedException("自訂順序檔不含篩選欄位");
        var parts = days.SelectMany(d => FlatNormFile.Partitions(_store.FlatFile(d), SplitsPerDay).Select(p => (d, p.Start, p.End))).ToList();
        var all = q.All.Select(Encoding.UTF8.GetBytes).ToArray();
        var any = q.Any.Select(Encoding.UTF8.GetBytes).ToArray();
        var not = q.Not.Select(Encoding.UTF8.GetBytes).ToArray();
        Regex? rx = q.Regex is null ? null : new Regex(q.Regex, q.RegexNonBacktracking ? RegexOptions.NonBacktracking : RegexOptions.None);
        var engine = rx is null ? "自訂順序檔掃描" : "自訂順序檔＋.NET Regex" + (q.RegexNonBacktracking ? " (NonBacktracking)" : "");

        return OrderedPartitions.Run(engine, parts.Count, (i, token) =>
        {
            var (day, start, end) = parts[i];
            var list = new List<long>();
            char[] chars = new char[4096];
            FlatNormFile.Scan(_store.FlatFile(day), start, end, (seq, text) =>
            {
                if (rx is not null)
                {
                    if (chars.Length < text.Length) chars = new char[text.Length * 2];
                    int n = Encoding.UTF8.GetChars(text, chars);
                    if (rx.IsMatch(chars.AsSpan(0, n))) list.Add(seq);
                    return true;
                }
                foreach (var t in all) if (text.IndexOf(t) < 0) return true;
                if (any.Length > 0)
                {
                    bool hit = false;
                    foreach (var t in any) if (text.IndexOf(t) >= 0) { hit = true; break; }
                    if (!hit) return true;
                }
                foreach (var t in not) if (text.IndexOf(t) >= 0) return true;
                list.Add(seq);
                return true;
            }, token);
            return list;
        }, Parallelism, ct);
    }
}
