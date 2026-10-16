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
            using var c = library.Create("效能", PlantConfig.Default);

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

            var caseBytes = Directory.EnumerateFiles(c.Folder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            output.WriteLine($"來源：{result.FileCount} 個檔案，{sourceBytes / 1024d / 1024d:N0} MB");
            output.WriteLine($"事件：{result.EventCount:N0} 筆");
            output.WriteLine($"匯入：{import.Elapsed.TotalSeconds:N1} 秒");
            output.WriteLine($"讀出所有事件：{load.Elapsed.TotalSeconds:N1} 秒");
            output.WriteLine($"案件大小：{caseBytes / 1024d / 1024d:N0} MB（原始 LOG 的 {(double)caseBytes / sourceBytes:N2} 倍）");

            Assert.Equal(result.EventCount, count);
            Assert.True(import.Elapsed + load.Elapsed < TimeSpan.FromMinutes(2), "超過 2 分鐘的目標");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
