using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using LogTrace.Engine.Knowledge;

namespace TechValidation.KnowledgeGraph;

/// <summary>
/// #89／ADR-0022：以本機 Ollama 的 embedding 做向量搜尋，和規則關聯圖、關鍵字比對比較。
/// 只送虛構資料到本機的 Ollama（127.0.0.1），不連外網。
/// </summary>
public sealed class OllamaEmbedder(string model, bool e5Prefix, string endpoint = "http://127.0.0.1:11434")
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public string Model => model;

    sealed record Request(string Model, IReadOnlyList<string> Input);
    sealed record Response(float[][] Embeddings);

    /// <summary>multilingual-e5 系列要在查詢與條目前加前綴；bge-m3 不用。</summary>
    public Task<float[][]> Queries(IReadOnlyList<string> texts) => Embed(texts.Select(t => e5Prefix ? "query: " + t : t).ToList());
    public Task<float[][]> Passages(IReadOnlyList<string> texts) => Embed(texts.Select(t => e5Prefix ? "passage: " + t : t).ToList());

    async Task<float[][]> Embed(IReadOnlyList<string> input)
    {
        var res = await Http.PostAsJsonAsync($"{endpoint}/api/embed", new Request(model, input));
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"{model}：{await res.Content.ReadAsStringAsync()}");
        var body = await res.Content.ReadFromJsonAsync<Response>() ?? throw new InvalidOperationException($"{model}：沒有回應內容");
        return body.Embeddings.Select(Normalize).ToArray();
    }

    static float[] Normalize(float[] v)
    {
        var n = MathF.Sqrt(v.Sum(x => x * x));
        return n == 0 ? v : v.Select(x => x / n).ToArray();
    }

    sealed record Ps(List<PsModel> Models);
    sealed record PsModel(string Name, long Size);

    /// <summary>Ollama /api/ps 回報的已載入模型大小（MB）；查不到時為 null。</summary>
    public static async Task<long?> LoadedSizeMb(string model, string endpoint = "http://127.0.0.1:11434")
    {
        var ps = await Http.GetFromJsonAsync<Ps>($"{endpoint}/api/ps");
        var hit = ps?.Models.FirstOrDefault(m => m.Name == model || m.Name == model + ":latest");
        return hit is null ? null : hit.Size / (1024 * 1024);
    }

    /// <summary>向量已正規化，cosine 相似度即內積（ADR-0022 第 4 點：不加套件、逐條比對）。</summary>
    public static float Cosine(float[] a, float[] b)
    {
        float s = 0;
        for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }
}

/// <summary>一個模型、一種條目文字的向量索引（知識庫條目與異常紀錄現象）。</summary>
public sealed class VectorIndex
{
    public required string Label { get; init; }
    public required List<(string Key, float[] V)> Kb { get; init; }
    public required List<(string Key, float[] V)> Incidents { get; init; }
    public required double BuildMs { get; init; }

    public static string KbText(KnowledgeEntry e, bool withCode) =>
        (withCode ? e.Code + " " : "") + e.Description + " " + e.Action;

    public static async Task<VectorIndex> Build(OllamaEmbedder m, KnowledgeBase kb, IReadOnlyList<Incident> incidents, bool withCode, string label)
    {
        var watch = Stopwatch.StartNew();
        var kv = await m.Passages(kb.Entries.Select(e => KbText(e, withCode)).ToList());
        var iv = await m.Passages(incidents.Select(i => i.Symptom).ToList());
        return new VectorIndex
        {
            Label = label,
            Kb = kb.Entries.Select((e, i) => (Found.KbKey(e), kv[i])).ToList(),
            Incidents = incidents.Select((x, i) => (x.Id, iv[i])).ToList(),
            BuildMs = watch.Elapsed.TotalMilliseconds,
        };
    }

    public static IReadOnlySet<string> Top(List<(string Key, float[] V)> items, float[] q, int k) =>
        items.Select(x => (x.Key, S: OllamaEmbedder.Cosine(q, x.V))).OrderByDescending(x => x.S).Take(k).Select(x => x.Key)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>正解在排名中的名次（1 起算）；用於檢查「差一點」的情況。</summary>
    public static int Rank(List<(string Key, float[] V)> items, float[] q, string key) =>
        items.Select(x => (x.Key, S: OllamaEmbedder.Cosine(q, x.V))).OrderByDescending(x => x.S).Select(x => x.Key).ToList().IndexOf(key) + 1;
}

public static class VectorReport
{
    sealed class Tally
    {
        public int Expected, Hit, Found, Exact, Cases;
        public void Add(IEnumerable<string> expected, IReadOnlySet<string> found, bool countExact = true)
        {
            var e = expected.ToHashSet(StringComparer.Ordinal);
            Expected += e.Count; Hit += e.Count(found.Contains); Found += found.Count;
            if (countExact) { Cases++; if (found.SetEquals(e)) Exact++; }
        }
        public string Recall => Expected == 0 ? "—" : $"{Hit}/{Expected}（{100.0 * Hit / Expected:0}%）";
        public string Precision => Found == 0 ? "—" : $"{Hit}/{Found}（{100.0 * Hit / Found:0}%）";
    }

    public sealed record ModelRun(string Model, string Source, VectorIndex WithCode, VectorIndex NoCode, float[][] Queries, double QueryEmbedMs);

    public static string Write(IReadOnlyList<Scenario> scenarios, IReadOnlyList<Found> graph, IReadOnlyList<Found> baseline, IReadOnlyList<ModelRun> runs, IReadOnlyDictionary<string, long> memory)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 知識檢索：向量搜尋的虛構情境集比較（#89，ADR-0015、ADR-0022）");
        sb.AppendLine();
        sb.AppendLine($"- 日期：{DateTime.Now:yyyy-MM-dd}");
        sb.AppendLine($"- 電腦：開發用電腦（{Environment.ProcessorCount} 執行緒，與 `summary-dev-pc.md` 相同）；.NET {Environment.Version}；Ollama（本機 127.0.0.1）");
        sb.AppendLine($"- 資料：`validation/knowledge-scenarios`（**虛構**：{scenarios.Count} 個情境、知識庫 {runs[0].WithCode.Kb.Count} 條、異常紀錄 {runs[0].WithCode.Incidents.Count} 筆）；規則關聯圖與關鍵字比對的結果同 `knowledge-graph-89-dev-pc.md`");
        sb.AppendLine("- 向量搜尋只看第一線的問題文字，不看 LOG；向量正規化後逐條計算 cosine 相似度（ADR-0022 第 4 點）");
        sb.AppendLine("- 條目文字：「含代碼」為「代碼 說明 處置」，「不含代碼」為「說明 處置」；multilingual-e5 加 `query: `／`passage: ` 前綴");
        sb.AppendLine("- 合併：「規則＋向量前 k」為規則關聯圖的結果加上向量搜尋前 k 條（聯集）");
        sb.AppendLine();
        sb.AppendLine("## 模型");
        sb.AppendLine();
        sb.AppendLine("| 模型 | 來源 | 建立向量（含代碼，ms） | 12 個問題的 embedding（暖機後中位數，ms） | Ollama 記憶體（MB） |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var r in runs)
            sb.AppendLine($"| {r.Model} | {r.Source} | {r.WithCode.BuildMs:0} | {r.QueryEmbedMs:0} | {(memory.TryGetValue(r.Model, out var mb) ? mb.ToString() : "—")} |");
        sb.AppendLine();
        sb.AppendLine("建立向量的時間含載入模型（Ollama 切換模型時會卸載前一個，載入約 3～5 秒）；查詢時間為模型已載入後，12 個問題一起送出、重複 5 次的中位數；記憶體為 Ollama `/api/ps` 回報的模型大小。");
        sb.AppendLine();

        Tally Sum(Func<int, IEnumerable<string>> expected, Func<int, IReadOnlySet<string>> found)
        {
            var t = new Tally();
            for (int i = 0; i < scenarios.Count; i++) t.Add(expected(i), found(i));
            return t;
        }
        IReadOnlySet<string> Union(IReadOnlySet<string> a, IReadOnlySet<string> b) => a.Concat(b).ToHashSet(StringComparer.Ordinal);

        sb.AppendLine("## 1. 知識庫條目");
        sb.AppendLine();
        sb.AppendLine("「完全選對」為找到的正好等於正解（含「應該沒有」的情境；向量搜尋固定取 k 條，正解為 0 條的情境一定不會完全選對）。");
        sb.AppendLine();
        sb.AppendLine("| 方法 | 召回 | 精確度 | 完全選對 |");
        sb.AppendLine("|---|---|---|---|");
        void Row(string label, Tally t) => sb.AppendLine($"| {label} | {t.Recall} | {t.Precision} | {t.Exact}/{t.Cases} |");
        Row("關鍵字比對", Sum(i => scenarios[i].Expected.Kb, i => baseline[i].Kb));
        Row("規則關聯圖", Sum(i => scenarios[i].Expected.Kb, i => graph[i].Kb));
        foreach (var r in runs)
            foreach (var (idx, text) in new[] { (r.WithCode, "含代碼"), (r.NoCode, "不含代碼") })
            {
                foreach (var k in new[] { 1, 3 })
                    Row($"{r.Model}（{text}）：只用向量，前 {k}", Sum(i => scenarios[i].Expected.Kb, i => VectorIndex.Top(idx.Kb, r.Queries[i], k)));
                foreach (var k in new[] { 1, 3 })
                    Row($"{r.Model}（{text}）：規則＋向量前 {k}", Sum(i => scenarios[i].Expected.Kb, i => Union(graph[i].Kb, VectorIndex.Top(idx.Kb, r.Queries[i], k))));
            }
        sb.AppendLine();

        sb.AppendLine("## 2. 異常紀錄（過去相似的異常，比對現象描述）");
        sb.AppendLine();
        sb.AppendLine("| 方法 | 召回 | 精確度 |");
        sb.AppendLine("|---|---|---|");
        void Row2(string label, Tally t) => sb.AppendLine($"| {label} | {t.Recall} | {t.Precision} |");
        Row2("關鍵字比對", Sum(i => scenarios[i].Expected.Incidents, i => baseline[i].Incidents));
        Row2("規則關聯圖", Sum(i => scenarios[i].Expected.Incidents, i => graph[i].Incidents));
        foreach (var r in runs)
            foreach (var k in new[] { 1, 3 })
            {
                Row2($"{r.Model}：只用向量，前 {k}", Sum(i => scenarios[i].Expected.Incidents, i => VectorIndex.Top(r.WithCode.Incidents, r.Queries[i], k)));
                Row2($"{r.Model}：規則＋向量前 {k}", Sum(i => scenarios[i].Expected.Incidents, i => Union(graph[i].Incidents, VectorIndex.Top(r.WithCode.Incidents, r.Queries[i], k))));
            }
        sb.AppendLine();

        sb.AppendLine("## 3. 各情境：知識庫正解在向量搜尋中的名次（含代碼）");
        sb.AppendLine();
        sb.AppendLine("名次 1 為最相似；知識庫共 " + runs[0].WithCode.Kb.Count + " 條。正解有兩條時以「、」分隔。");
        sb.AppendLine();
        sb.AppendLine("| 情境 | 第一線的描述 | " + string.Join(" | ", runs.Select(r => r.Model)) + " | 規則關聯圖 |");
        sb.AppendLine("|---|---|" + string.Concat(runs.Select(_ => "---|")) + "---|");
        for (int i = 0; i < scenarios.Count; i++)
        {
            var s = scenarios[i];
            var cells = runs.Select(r => s.Expected.Kb.Count == 0 ? "（無正解）" : string.Join("、", s.Expected.Kb.Select(k => VectorIndex.Rank(r.WithCode.Kb, r.Queries[i], k))));
            var g = s.Expected.Kb.Count == 0 ? "（無正解）" : $"{s.Expected.Kb.Count(graph[i].Kb.Contains)}/{s.Expected.Kb.Count}";
            sb.AppendLine($"| {s.Id} | {s.Question} | {string.Join(" | ", cells)} | {g} |");
        }
        return sb.ToString();
    }
}
