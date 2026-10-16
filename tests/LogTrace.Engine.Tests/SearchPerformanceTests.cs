using System.Diagnostics;
using LogTrace.Engine.Cases;
using LogTrace.Engine.Plants;
using LogTrace.Engine.Search;
using Xunit.Abstractions;

namespace LogTrace.Engine.Tests;

/// <summary>
/// 全文索引與搜尋的效能（#21；ADR-0010 第 5 點：一般關鍵字一天份 2 秒、七天份 10 秒內開始顯示結果；
/// ADR-0011 第 4 點：案件 ≤ 原始 LOG 5 倍）。資料量大，不放在 repo：設定環境變數後才執行，否則直接結束。
/// <list type="bullet">
/// <item>LOGTRACE_PERF_SEARCH_SOURCE：虛構 LOG（一天或多天，例如技術驗證產生的 tv-data\logs\20300107 或 tv-data\logs）。</item>
/// <item>LOGTRACE_PERF_WORK：放案件的工作資料夾（需要約原始資料 4 倍的空間），結束後刪除（LOGTRACE_PERF_KEEP=1 時保留）。</item>
/// </list>
/// 「開始顯示結果」以搜尋完成的時間量（結果一次顯示），比目標更嚴格。
/// </summary>
[Trait("Category", "Perf")]
public sealed class SearchPerformanceTests(ITestOutputHelper output)
{
    static readonly (string Label, SearchQuery Query)[] Queries =
    [
        ("常見詞（2 字）「卸貨」", new() { AllOf = "卸貨" }),
        ("常見詞（3 字以上）「CMD_NO」", new() { AllOf = "CMD_NO" }),
        ("罕見詞（命令編號）", new() { AllOf = "00010927" }),
        ("兩個詞 AND", new() { AllOf = "卸貨 A-17" }),
        ("罕見詞 AND 常見詞", new() { AllOf = "00010927 CMD_NO" }),
        ("任一包含", new() { AnyOf = "出庫循環 卸貨完成" }),
        ("不包含", new() { AllOf = "CMD_NO", NoneOf = "卸貨" }),
        ("區分大小寫", new() { AllOf = "SubmitCompleted", CaseSensitive = true }),
        ("不存在的詞", new() { AllOf = "不存在的虛構字串" }),
        ("正規表示式", new() { AllOf = @"CMD_NO=0001092\d", Regex = true }),
    ];

    [Fact]
    public async Task 建立全文索引_索引與掃描結果相同_量測搜尋時間與磁碟用量()
    {
        var source = Environment.GetEnvironmentVariable("LOGTRACE_PERF_SEARCH_SOURCE");
        var work = Environment.GetEnvironmentVariable("LOGTRACE_PERF_WORK");
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(work))
        {
            output.WriteLine("未設定 LOGTRACE_PERF_SEARCH_SOURCE／LOGTRACE_PERF_WORK，略過。");
            return;
        }

        var root = Path.Combine(work, "perf-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var sourceBytes = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            var c = new CaseLibrary(root).Create("效能", PlantConfig.Default);
            var import = Stopwatch.StartNew();
            var imported = c.Import(source);
            import.Stop();
            var days = c.IndexState().Total;
            output.WriteLine($"來源：{imported.FileCount} 個檔案，{Mb(sourceBytes)}，{days} 天，{imported.EventCount:N0} 筆事件；基本匯入 {import.Elapsed.TotalSeconds:N1} 秒");

            // 索引建立期間搜尋：結果與掃描相同（只量第一個查詢，避免拖慢建立）
            var probe = Queries[2].Query;
            var expectedProbe = c.Search(probe, SearchPath.ScanOnly, null).Sequences;
            var build = Stopwatch.StartNew();
            var task = Task.Run(() => c.BuildIndex());
            int during = 0;
            while (!task.IsCompleted)
            {
                var watch = Stopwatch.StartNew();
                var hits = c.Search(probe, SearchPath.Auto, null);
                Assert.Equal(expectedProbe, hits.Sequences);
                output.WriteLine($"  建立中搜尋：{hits.Sequences.Count:N0} 筆，{watch.Elapsed.TotalSeconds:N2} 秒（索引 {hits.IndexedDays} 天、掃描 {hits.ScannedDays} 天）");
                during++;
                await Task.Delay(2000);
            }
            var built = await task;
            build.Stop();
            output.WriteLine($"全文索引：{built.BuiltDays}/{built.TotalDays} 天，{build.Elapsed.TotalSeconds:N1} 秒，超過上限：{built.LimitReached}；建立中搜尋 {during} 次");

            // 磁碟用量：關閉案件後量
            var folder = c.Folder;
            c.Dispose();
            long Size(string path) => Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0;
            var caseBytes = Size(folder);
            var indexBytes = Size(Path.Combine(folder, "index"));
            output.WriteLine($"案件大小（關閉後）：{Mb(caseBytes)}，原始 LOG 的 {(double)caseBytes / sourceBytes:N2} 倍（上限 5 倍）；其中全文索引 {Mb(indexBytes)}（{(double)indexBytes / sourceBytes:N2} 倍）");
            Assert.True(caseBytes < sourceBytes * 5, "超過原始 LOG 的 5 倍");

            using var reopened = Case.Open(folder);
            reopened.Search(new SearchQuery { AllOf = "熱身" }, SearchPath.ScanOnly, null); // 熱身
            var limit = TimeSpan.FromSeconds(days <= 1 ? 2 : 10);
            output.WriteLine($"| 查詢 | 符合筆數 | 自動（索引／掃描天數） | 一律掃描 |");
            foreach (var (label, query) in Queries)
            {
                var a = Stopwatch.StartNew();
                var auto = reopened.Search(query, SearchPath.Auto, null);
                a.Stop();
                var s = Stopwatch.StartNew();
                var scan = reopened.Search(query, SearchPath.ScanOnly, null);
                s.Stop();
                Assert.Equal(scan.Sequences, auto.Sequences);
                output.WriteLine($"| {label} | {auto.Sequences.Count:N0} | {a.Elapsed.TotalSeconds:N2} 秒（{auto.IndexedDays}／{auto.ScannedDays}） | {s.Elapsed.TotalSeconds:N2} 秒 |");
                if (!query.Regex) Assert.True(a.Elapsed < limit, $"{label} 超過 {limit.TotalSeconds} 秒");
            }

            // 刪除索引後重建：結果不變
            var before = reopened.Search(probe);
            reopened.DeleteIndex();
            var rebuild = Stopwatch.StartNew();
            reopened.BuildIndex();
            rebuild.Stop();
            Assert.Equal(before, reopened.Search(probe));
            output.WriteLine($"刪除後重建索引：{rebuild.Elapsed.TotalSeconds:N1} 秒，結果不變");
        }
        finally
        {
            if (Directory.Exists(root) && Environment.GetEnvironmentVariable("LOGTRACE_PERF_KEEP") != "1")
            {
                foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(root, recursive: true);
            }
        }
    }

    static string Mb(long bytes) => $"{bytes / 1024d / 1024d:N0} MB";
}
