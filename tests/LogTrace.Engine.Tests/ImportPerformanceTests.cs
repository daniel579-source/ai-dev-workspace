using System.Diagnostics;
using LogTrace.Engine.Cases;
using LogTrace.Engine.Plants;
using Xunit.Abstractions;

namespace LogTrace.Engine.Tests;

/// <summary>
/// 一天份的基本匯入時間（ADR-0011 第 4 點：一天約 500 MB，目標 2 分鐘內可以瀏覽）。
/// 資料量大，不放在 repo：設定環境變數後才執行，否則直接結束。
/// <list type="bullet">
/// <item>LOGTRACE_PERF_SOURCE：一天份的虛構 LOG（例如技術驗證產生的 tv-data\logs\20300107）。</item>
/// <item>LOGTRACE_PERF_WORK：放案件的工作資料夾（需要約原始資料 3 倍的空間），結束後刪除。</item>
/// </list>
/// </summary>
[Trait("Category", "Perf")]
public sealed class ImportPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void 一天份基本匯入並讀出所有事件()
    {
        var source = Environment.GetEnvironmentVariable("LOGTRACE_PERF_SOURCE");
        var work = Environment.GetEnvironmentVariable("LOGTRACE_PERF_WORK");
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(work))
        {
            output.WriteLine("未設定 LOGTRACE_PERF_SOURCE／LOGTRACE_PERF_WORK，略過。");
            return;
        }

        var root = Path.Combine(work, "perf-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var sourceBytes = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            var library = new CaseLibrary(root);
            var c = library.Create("效能", PlantConfig.Default);

            var import = Stopwatch.StartNew();
            var result = c.Import(source);
            import.Stop();

            var load = Stopwatch.StartNew();
            long count = 0, next = 1;
            while (true)
            {
                var page = c.GetEvents(next, 10_000);
                if (page.Count == 0) break;
                count += page.Count;
                next = page[^1].Sequence + 1;
            }
            load.Stop();

            output.WriteLine($"來源：{result.FileCount} 個檔案，{sourceBytes / 1024d / 1024d:N0} MB");
            output.WriteLine($"事件：{result.EventCount:N0} 筆");
            output.WriteLine($"匯入：{import.Elapsed.TotalSeconds:N1} 秒");
            output.WriteLine($"讀出所有事件：{load.Elapsed.TotalSeconds:N1} 秒");

            Assert.Equal(result.EventCount, count);
            // ADR-0011 第 4 點：平常量級（一天約 500 MB）2 分鐘、最大量級（一天約 1.5 GB）3 分鐘內可以瀏覽（2026-10-19 #47）
            var limit = TimeSpan.FromMinutes(sourceBytes > 1024L * 1024 * 1024 ? 3 : 2);
            Assert.True(import.Elapsed + load.Elapsed < limit, $"超過 {limit.TotalMinutes} 分鐘的目標");

            // 搜尋（ADR-0010 第 5 點：一般關鍵字，一天份 2 秒內）；第一次搜尋前先執行一次熱身
            c.Search(new Search.SearchQuery { AllOf = "熱身" });
            foreach (var (label, query) in new (string, Search.SearchQuery)[]
            {
                ("常見詞「卸貨」", new() { AllOf = "卸貨" }),
                ("罕見詞（命令編號）", new() { AllOf = "00010927" }),
                ("兩個詞 AND", new() { AllOf = "卸貨 A-17" }),
                ("任一包含", new() { AnyOf = "出庫循環 卸貨完成" }),
                ("不包含", new() { AllOf = "CMD_NO", NoneOf = "卸貨" }),
                ("區分大小寫", new() { AllOf = "SubmitCompleted", CaseSensitive = true }),
                ("不存在的詞", new() { AllOf = "不存在的虛構字串" }),
            })
            {
                var watch = Stopwatch.StartNew();
                var hits = c.Search(query);
                watch.Stop();
                output.WriteLine($"搜尋 {label}：{hits.Count:N0} 筆，{watch.Elapsed.TotalSeconds:N2} 秒");
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"{label} 超過 2 秒");
            }

            // 修改時間偏移量（ADR-0003 第 5 點）：重新解析並排序整個案件（#29 補量）
            var correction = Stopwatch.StartNew();
            c.SetTimeCorrection(Events.TimeCorrection.Create(TimeSpan.Zero, [KeyValuePair.Create("PLC", TimeSpan.FromSeconds(1))]));
            correction.Stop();
            output.WriteLine($"修改時間偏移量（PLC +1 秒，重新排序）：{correction.Elapsed.TotalSeconds:N1} 秒");

            // 案件大小：關閉案件後才量（開啟中有 WAL 暫存檔，關閉時併回 case.db）
            var folder = c.Folder;
            c.Dispose();
            var caseBytes = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            output.WriteLine($"案件大小（關閉後）：{caseBytes / 1024d / 1024d:N0} MB（原始 LOG 的 {(double)caseBytes / sourceBytes:N2} 倍；上限 5 倍）");
            Assert.True(caseBytes < sourceBytes * 5, "超過原始 LOG 的 5 倍");
        }
        finally
        {
            // LOGTRACE_PERF_KEEP=1 時保留案件，用來分析各資料表的大小
            if (Directory.Exists(root) && Environment.GetEnvironmentVariable("LOGTRACE_PERF_KEEP") != "1")
            {
                foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
