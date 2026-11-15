// #89：ADR-0015「規則關聯圖」的虛構情境集驗證。不是正式產品；資料全部是虛構的（validation/knowledge-scenarios）。
// 用法：dotnet run -c Release --project validation/src/TechValidation.KnowledgeGraph -- --data validation/knowledge-scenarios --report validation/results/knowledge-graph-89-<電腦名稱>.md
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LogTrace.Engine.Cases;
using LogTrace.Engine.Events;
using LogTrace.Engine.Knowledge;
using LogTrace.Engine.Parsing;
using LogTrace.Engine.Playback;
using LogTrace.Engine.Plants;
using TechValidation.KnowledgeGraph;

string Arg(string name, string fallback) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault() ?? fallback;
var data = Path.GetFullPath(Arg("--data", "validation/knowledge-scenarios"));
var report = Arg("--report", "");
var windowMinutes = int.Parse(Arg("--window", "5"));
var hops = int.Parse(Arg("--hops", "1"));
const int Repeat = 20;

// ---- 載入虛構資料：建立暫時案件並匯入 ----
var doc = PlantConfigFile.Load(Path.Combine(data, "plant.json"));
var work = Path.Combine(Path.GetTempPath(), "kg89-" + Guid.NewGuid().ToString("N")[..8]);
try
{
    var c = new CaseLibrary(work).Create("kg89", doc.ToPlantConfig(), doc, Path.Combine(data, "plant.json"));
    c.Import(Path.Combine(data, "logs"));
    var events = new List<LogEvent>();
    for (long next = 1; ;)
    {
        var page = c.GetEvents(next, 10_000);
        if (page.Count == 0) break;
        events.AddRange(page);
        next = page[^1].Sequence + 1;
    }
    var times = events.ToDictionary(e => e.Sequence, e => e.Timestamp);
    var model = new WorldModel(c.GetEffects(), doc.Layout, c.Info.Plant.PresenceSignal);
    var gaps = LogGaps.Find(events, doc.GapThresholds);
    var stalls = Stalls.Find(model, s => times.TryGetValue(s, out var t) ? t : null, events[^1].Timestamp, doc.EffectiveStallThresholdSeconds, gaps);
    var kb = KnowledgeBase.Load(Path.Combine(data, "plant.kb.csv")) ?? throw new InvalidOperationException("沒有知識庫");
    var safety = SafetyList.Load(Path.Combine(data, "safety.csv"));
    var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    var incidents = JsonSerializer.Deserialize<List<Incident>>(File.ReadAllText(Path.Combine(data, "incidents.json")), json)!;
    var scenarios = JsonSerializer.Deserialize<List<Scenario>>(File.ReadAllText(Path.Combine(data, "scenarios.json")), json)!;

    var graph = new RuleGraph(model, doc.Layout, times, gaps, stalls, kb, safety, incidents, TimeSpan.FromMinutes(windowMinutes), hops);
    var baseline = new KeywordBaseline(doc.Layout, kb, safety, incidents);

    var rows = new List<(Scenario S, Found G, Found B, double Ms)>();
    foreach (var s in scenarios)
    {
        var found = graph.Expand(s);
        var ms = new List<double>();
        for (int i = 0; i < Repeat; i++)
        {
            var watch = Stopwatch.StartNew();
            graph.Expand(s);
            ms.Add(watch.Elapsed.TotalMilliseconds);
        }
        rows.Add((s, found, baseline.Find(s), ms.Order().ElementAt(ms.Count / 2)));
    }

    var text = Report.Write(rows, events.Count, windowMinutes, hops, Repeat);
    Console.WriteLine(text);
    if (report.Length > 0)
    {
        File.WriteAllText(report, text, new UTF8Encoding(false));
        Console.WriteLine($"報告：{report}");
    }
}
finally
{
    try { Directory.Delete(work, true); } catch { /* 暫時案件 */ }
}
