using System.Diagnostics;
using DuckDB.NET.Data;
using TechValidation.Core;

namespace TechValidation.Bench;

/// <summary>比較對象 D：DuckDB（ADR-0011 保留為實測比較對象）。只放正規化文字，不含篩選欄位。</summary>
public sealed class DuckDbBackend(string path)
{
    public string Path { get; } = path;

    public double Load(CaseStore store, IReadOnlyList<int> days)
    {
        if (File.Exists(Path)) File.Delete(Path);
        var sw = Stopwatch.StartNew();
        using var c = new DuckDBConnection($"Data Source={Path}");
        c.Open();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE norm(day INTEGER, seq BIGINT, ntext VARCHAR)";
            cmd.ExecuteNonQuery();
        }
        using (var app = c.CreateAppender("norm"))
        {
            foreach (var d in days)
            {
                var file = store.FlatFile(d);
                foreach (var (start, end) in FlatNormFile.Partitions(file, 1))
                {
                    FlatNormFile.Scan(file, start, end, (seq, utf8) =>
                    {
                        app.CreateRow().AppendValue((int?)d).AppendValue((long?)seq).AppendValue(System.Text.Encoding.UTF8.GetString(utf8)).EndRow();
                        return true;
                    });
                }
            }
        }
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "CHECKPOINT";
            cmd.ExecuteNonQuery();
        }
        return sw.Elapsed.TotalSeconds;
    }

    public SearchResult Search(SearchQuery q, IReadOnlyList<int> days)
    {
        q = q.Normalized();
        var res = new SearchResult { Engine = q.Regex is null ? "DuckDB contains" : "DuckDB regexp_matches (RE2)" };
        var sw = Stopwatch.StartNew();
        using var c = new DuckDBConnection($"Data Source={Path};ACCESS_MODE=READ_ONLY");
        c.Open();
        using var cmd = c.CreateCommand();
        var where = new List<string> { $"day IN ({string.Join(",", days)})" };
        void P(object v) => cmd.Parameters.Add(new DuckDBParameter(v));
        if (q.Regex is not null) { where.Add("regexp_matches(ntext, ?)"); P(q.Regex); }
        foreach (var t in q.All) { where.Add("contains(ntext, ?)"); P(t); }
        if (q.Any.Length > 0)
        {
            where.Add("(" + string.Join(" OR ", q.Any.Select(_ => "contains(ntext, ?)")) + ")");
            foreach (var t in q.Any) P(t);
        }
        foreach (var t in q.Not) { where.Add("NOT contains(ntext, ?)"); P(t); }
        cmd.CommandText = $"SELECT seq FROM norm WHERE {string.Join(" AND ", where)} ORDER BY seq";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (res.Seqs.Count == 0) res.FirstResultMs = sw.Elapsed.TotalMilliseconds;
            res.Seqs.Add(r.GetInt64(0));
        }
        res.TotalMs = sw.Elapsed.TotalMilliseconds;
        return res;
    }
}
