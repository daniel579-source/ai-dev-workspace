using System.Text;
using System.Text.Json;
using LogTrace.Engine.Knowledge;
using LogTrace.Engine.Search;

namespace TechValidation.KnowledgeGraph;

/// <summary>「用詞不同」情境集的一題：第一線的口語描述與正解代碼（全部虛構，validation/knowledge-wording）。</summary>
public sealed record WordingQuestion(string Id, string Question, string Answer, string Kind, bool CrossLingual);

/// <summary>
/// #89／ADR-0022：只看問題文字（沒有警報代碼、不看 LOG），比較關鍵字搜尋、字元雙字重疊與 Ollama embedding 的排名。
/// </summary>
public static class Wording
{
    sealed record Method(string Name, Func<int, string, int> Rank);

    /// <summary>字元雙字（bigram）重疊比例：詞彙比對的基準，不需要斷詞。</summary>
    static HashSet<string> Bigrams(string text)
    {
        var t = TextNormalizer.Normalize(text).Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c)).ToArray();
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i + 1 < t.Length; i++) set.Add(new string(t, i, 2));
        return set;
    }

    /// <summary>依分數由高到低排名，回傳正解的名次（1 起算）；同分時排在同分者的最後（保守）。</summary>
    static int RankOf(IReadOnlyList<double> scores, int answer) =>
        scores.Count(s => s > scores[answer]) + scores.Count(s => s == scores[answer]);

    public static async Task<string> Run(string dir, IReadOnlyList<(string Model, bool E5, string Source)> models)
    {
        var kb = KnowledgeBase.Load(Path.Combine(dir, "kb.csv")) ?? throw new InvalidOperationException("沒有 kb.csv");
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var qs = JsonSerializer.Deserialize<List<WordingQuestion>>(File.ReadAllText(Path.Combine(dir, "questions.json")), json)!;
        var entries = kb.Entries;
        int Index(string code) => entries.ToList().FindIndex(e => e.Code == code);
        foreach (var q in qs) if (Index(q.Answer) < 0) throw new InvalidOperationException($"{q.Id}：知識庫沒有 {q.Answer}");

        // LogTrace 知識庫面板的搜尋（整句當關鍵字，ADR-0013 第 2 點）：找到就算第 1 名，找不到為 0
        var search = qs.Select(q => kb.Search(q.Question)).ToList();

        var kbBigrams = entries.Select(e => Bigrams(e.Description + e.Action)).ToList();
        var methods = new List<Method>
        {
            new("字元雙字重疊（說明＋處置）", (i, _) =>
            {
                var qb = Bigrams(qs[i].Question);
                var scores = kbBigrams.Select(b => qb.Count == 0 ? 0.0 : (double)qb.Count(b.Contains) / qb.Count).ToList();
                return scores[Index(qs[i].Answer)] == 0 ? 0 : RankOf(scores, Index(qs[i].Answer));
            }),
        };

        var info = new List<(string Model, string Source, long? Mb)>();
        foreach (var (model, e5, source) in models)
        {
            var m = new OllamaEmbedder(model, e5);
            var q = await m.Queries(qs.Select(x => x.Question).ToList());
            foreach (var withCode in new[] { true, false })
            {
                var v = await m.Passages(entries.Select(e => VectorIndex.KbText(e, withCode)).ToList());
                methods.Add(new($"{model}（{(withCode ? "含代碼" : "不含代碼")}）", (i, _) =>
                    RankOf(v.Select(x => (double)OllamaEmbedder.Cosine(q[i], x)).ToList(), Index(qs[i].Answer))));
            }
            info.Add((model, source, await OllamaEmbedder.LoadedSizeMb(model)));
        }

        var ranks = methods.Select(m => qs.Select((q, i) => m.Rank(i, q.Question)).ToArray()).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("# 知識檢索：「用詞不同」虛構情境集的向量搜尋比較（#89，ADR-0015、ADR-0022）");
        sb.AppendLine();
        sb.AppendLine($"- 日期：{DateTime.Now:yyyy-MM-dd}");
        sb.AppendLine($"- 電腦：開發用電腦（{Environment.ProcessorCount} 執行緒）；.NET {Environment.Version}；Ollama（本機 127.0.0.1）");
        sb.AppendLine($"- 資料：`validation/knowledge-wording`（**虛構**）：知識庫 {entries.Count} 條（其中日文 {entries.Count(e => e.Description.Any(c => c is >= '぀' and <= 'ヿ'))} 條），題目 {qs.Count} 題；每題只有第一線的一句描述，沒有警報代碼、不看 LOG，正解為一條知識庫條目");
        sb.AppendLine($"- 題目類型：{string.Join("、", qs.GroupBy(q => q.Kind).Select(g => $"{g.Key} {g.Count()} 題"))}；正解為日文條目（跨語言）{qs.Count(q => q.CrossLingual)} 題");
        sb.AppendLine("- 知識庫刻意放入意思相近的條目（例如過載與過熱、遮斷逾時與無反應、二重入庫與空出庫、變頻器異常與インバータ過電流）");
        sb.AppendLine("- 指標：前 1、3、5 名的召回（正解排在前 k 名的題數比例），以及 MRR（正解名次倒數的平均，找不到記 0；越接近 1 越好）。同分時名次記在同分者的最後");
        sb.AppendLine("- 題目由開發者撰寫，可能偏向開發者的寫法；之後應加入工程師提供的真實問法");
        sb.AppendLine();

        sb.AppendLine("## 1. LogTrace 知識庫面板的關鍵字搜尋（整句）");
        sb.AppendLine();
        var hit = qs.Select((q, i) => search[i].Any(e => e.Code == q.Answer)).Count(b => b);
        sb.AppendLine($"整句當成關鍵字時，找到正解 {hit}/{qs.Count} 題，有結果的題目 {search.Count(s => s.Count > 0)}/{qs.Count} 題。第一線的句子很少和知識庫原文一字不差，所以只作為參考。");
        sb.AppendLine();

        string Pct(int n, int d) => d == 0 ? "—" : $"{100.0 * n / d:0}%";
        string Mrr(IEnumerable<int> r) { var l = r.ToList(); return l.Count == 0 ? "—" : $"{l.Average(x => x == 0 ? 0 : 1.0 / x):0.00}"; }
        void Table(string title, Func<WordingQuestion, bool> filter)
        {
            var idx = qs.Select((q, i) => (q, i)).Where(x => filter(x.q)).Select(x => x.i).ToList();
            sb.AppendLine($"## {title}（{idx.Count} 題）");
            sb.AppendLine();
            sb.AppendLine("| 方法 | 前 1 | 前 3 | 前 5 | MRR |");
            sb.AppendLine("|---|---|---|---|---|");
            for (int m = 0; m < methods.Count; m++)
            {
                var r = idx.Select(i => ranks[m][i]).ToList();
                string At(int k) => Pct(r.Count(x => x >= 1 && x <= k), r.Count);
                sb.AppendLine($"| {methods[m].Name} | {At(1)} | {At(3)} | {At(5)} | {Mrr(r)} |");
            }
            sb.AppendLine();
        }
        Table("2. 全部題目", _ => true);
        int n = 3;
        foreach (var kind in qs.Select(q => q.Kind).Distinct())
            Table($"{n++}. 類型：{kind}", q => q.Kind == kind);
        Table($"{n++}. 正解為中文條目", q => !q.CrossLingual);
        Table($"{n++}. 正解為日文條目（跨語言）", q => q.CrossLingual);

        sb.AppendLine($"## {n++}. 模型");
        sb.AppendLine();
        sb.AppendLine("| 模型 | 來源 | Ollama 記憶體（MB） |");
        sb.AppendLine("|---|---|---|");
        foreach (var (model, source, mb) in info) sb.AppendLine($"| {model} | {source} | {(mb?.ToString() ?? "—")} |");
        sb.AppendLine();

        sb.AppendLine($"## {n++}. 各題的正解名次");
        sb.AppendLine();
        sb.AppendLine("0 表示找不到（沒有任何重疊）。知識庫共 " + entries.Count + " 條。");
        sb.AppendLine();
        sb.AppendLine("| 題目 | 類型 | 第一線的描述 | 正解 | " + string.Join(" | ", methods.Select(m => m.Name)) + " |");
        sb.AppendLine("|---|---|---|---|" + string.Concat(methods.Select(_ => "---|")));
        for (int i = 0; i < qs.Count; i++)
        {
            var q = qs[i];
            var e = entries[Index(q.Answer)];
            sb.AppendLine($"| {q.Id} | {q.Kind}{(q.CrossLingual ? "（跨語言）" : "")} | {q.Question} | {e.Code}：{e.Description} | {string.Join(" | ", ranks.Select(r => r[i]))} |");
        }
        return sb.ToString();
    }
}
