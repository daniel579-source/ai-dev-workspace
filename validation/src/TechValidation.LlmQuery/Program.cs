using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.ML.OnnxRuntimeGenAI;
using TechValidation.LlmQuery;

// #55 技術驗證（ADR-0013 第 5、8 點）：本機 LLM 把自然語言問題轉成搜尋條件。
// 用法：
//   dotnet run -c Release --project src/TechValidation.LlmQuery -- --model D:\_case\models\phi-4-mini-cpu-int4 --family phi --name Phi-4-mini
//       --questions llm-query\questions.json --out results\llm-query-55-phi.json [--limit 5]
//   dotnet run -c Release --project src/TechValidation.LlmQuery -- --summarize results\llm-query-55-phi.json results\llm-query-55-qwen.json --report results\llm-query-55-dev-pc.md
//   混合做法（第二輪）：加上 --hybrid；保留測試集加上 --examples llm-query\questions.json（範例沿用主題目檔）。
//   只檢查規則：--check-rules --questions llm-query\questions.json
// 每個模型跑 4 種組合：輸出（只用提示詞／以 JSON schema 限制）×提示詞（有範例／沒有範例）。只用 CPU。
var opts = Args.Parse(args);
if (opts.TryGetValue("summarize", out var summarizeList))
{
    Report.Write(summarizeList.Split(';', StringSplitOptions.RemoveEmptyEntries), opts["report"], opts.TryGetValue("note", out var note) ? note : null);
    return 0;
}

var questionsDoc = JsonNode.Parse(File.ReadAllText(opts["questions"]))!;
var caseDate = questionsDoc["caseDate"]!.GetValue<string>();
// 第三輪：--expand（隱含 --hybrid）：延伸詞＋程式檢查，只跑有範例的組合；--case-log 為虛構案件 LOG
var expand = opts.ContainsKey("expand");
var hybrid = expand || opts.ContainsKey("hybrid");
var caseLog = expand ? new CaseLog(opts.TryGetValue("case-log", out var caseLogPath) ? caseLogPath : "llm-query/case-log.json") : null;
var examplesDoc = opts.TryGetValue("examples", out var examplesPath) ? JsonNode.Parse(File.ReadAllText(examplesPath))! : questionsDoc;
var examples = hybrid
    ? examplesDoc[expand ? "expandExamples" : "hybridExamples"]!.AsArray()
        .Select(e => (Rules.Extract(e!["question"]!.GetValue<string>(), caseDate).Remainder, Prompt.Answer(e["answer"]))).ToList()
    : examplesDoc["examples"]!.AsArray()
        .Select(e => (e!["question"]!.GetValue<string>(), Prompt.Answer(e["expect"]))).ToList();
var questions = questionsDoc["questions"]!.AsArray()
    .Select(q => (Id: q!["id"]!.GetValue<string>(), Text: q["question"]!.GetValue<string>(), Expect: Query.Parse(q["expect"]),
        ExpectSuggest: q["expectSuggest"] is JsonArray es ? es.Select(x => x!.GetValue<string>()).ToList() : null)).ToList();
if (opts.TryGetValue("limit", out var limit)) questions = questions.Take(int.Parse(limit, CultureInfo.InvariantCulture)).ToList();
var family = opts["family"].Equals("qwen", StringComparison.OrdinalIgnoreCase) ? ModelFamily.Qwen : ModelFamily.Phi;
var system = hybrid ? Prompt.HybridSystem(expand) : Prompt.System(caseDate);
var schema = hybrid ? Prompt.HybridSchema(expand) : Prompt.Schema(caseDate);

if (opts.ContainsKey("check-rules"))
{
    // 只比對規則負責的部分（時間起訖與設備編號）；不能轉換的題目不算
    int ok = 0, total = 0;
    foreach (var q in questions.Where(q => !q.Expect.Unsupported))
    {
        var ext = Rules.Extract(q.Text, caseDate);
        var actual = q.Expect with { From = ext.From, To = ext.To, Fields = ext.Fields };
        var parts = Scoring.Compare(q.Expect, actual);
        total++;
        if (parts.Values.All(x => x)) ok++;
        else Console.WriteLine($"{q.Id} {string.Join("、", parts.Where(p => !p.Value).Select(p => p.Key))}：{ext.Remainder}｜{ext.From:HH:mm}～{ext.To:HH:mm}｜{string.Join(",", ext.Fields.Select(f => f.Kind + "=" + f.Value))}");
    }
    foreach (var e in examples) Console.WriteLine($"範例：{e.Item1}");
    Console.WriteLine($"規則部分正確 {ok}／{total}");
    return 0;
}

var load = Stopwatch.StartNew();
using var config = new Config(opts["model"]);
config.ClearProviders(); // 只用 CPU（2026-11-27 Jev 0.86：GPU、NPU 留到目標筆電）
using var model = new Model(config);
using var tokenizer = new Tokenizer(model);
load.Stop();
Console.WriteLine($"載入 {opts["name"]}：{load.Elapsed.TotalSeconds:N1} 秒");

var runs = new JsonArray();
foreach (var guided in new[] { false, true })
foreach (var fewShot in expand ? new[] { true } : new[] { true, false }) // 第三輪只跑有範例（使用者決定 2026-11-27）
{
    var label = $"{(guided ? "schema 限制" : "只用提示詞")}／{(fewShot ? "有範例" : "沒有範例")}";
    var items = new JsonArray();
    foreach (var q in questions)
    {
        var ext = hybrid ? Rules.Extract(q.Text, caseDate) : null;
        var prompt = Prompt.Build(family, system, fewShot ? examples : [], ext?.Remainder ?? q.Text);
        using var seq = tokenizer.Encode(prompt);
        var promptTokens = seq[0].Length;
        using var gp = new GeneratorParams(model);
        gp.SetSearchOption("max_length", promptTokens + 320);
        gp.SetSearchOption("do_sample", false);
        if (guided) gp.SetGuidance("json_schema", schema);
        var sw = Stopwatch.StartNew();
        using var gen = new Generator(model, gp);
        gen.AppendTokenSequences(seq);
        using var stream = tokenizer.CreateStream();
        var text = new StringBuilder();
        double? firstToken = null;
        int outTokens = 0;
        while (!gen.IsDone())
        {
            gen.GenerateNextToken();
            var tokens = gen.GetSequence(0);
            text.Append(stream.Decode(tokens[^1]));
            outTokens++;
            firstToken ??= sw.Elapsed.TotalSeconds;
            if (!guided && text.Length > 0 && Scoring.ExtractJson(text.ToString()) is not null) break; // 已經有完整的 JSON
        }
        sw.Stop();
        var raw = text.ToString();
        string? error = null;
        Query? actual = null;
        JsonNode? parsed = null;
        try
        {
            var json = Scoring.ExtractJson(raw) ?? throw new FormatException("輸出中沒有完整的 JSON 物件");
            parsed = JsonNode.Parse(json);
            actual = Query.Parse(parsed);
            if (ext is not null && !actual.Unsupported)
            {
                // 混合做法：時間與編號用規則的結果；含佔位符的關鍵字丟掉
                static List<string> Clean(IEnumerable<string> xs) => xs.Where(x => !x.Contains(Rules.TimeMark) && !x.Contains(Rules.IdMark)).ToList();
                actual = actual with { AllOf = Clean(actual.AllOf), AnyOf = Clean(actual.AnyOf), NoneOf = Clean(actual.NoneOf), From = ext.From, To = ext.To, Fields = ext.Fields };
            }
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException or ArgumentException) // ArgumentException：模型輸出重複的欄位名稱
        {
            error = ex.Message;
            actual = null; parsed = null; // 格式錯誤時整題算錯，也不讀延伸詞
        }
        var parts = actual is null ? Scoring.Parts.ToDictionary(p => p, _ => false) : Scoring.Compare(q.Expect, actual);
        var exact = actual is not null && parts.Values.All(x => x);
        var extra = new JsonObject();
        if (expand)
        {
            // 檢查前的結果另外記錄；exact／parts 改為程式檢查後的結果
            extra["exactRaw"] = exact;
            extra["partsRaw"] = new JsonObject(parts.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value)));
            var hints = new List<string>();
            if (actual is not null)
            {
                var checkedResult = Checks.Apply(actual, q.Text);
                actual = checkedResult.Query;
                hints = checkedResult.Hints.ToList();
                parts = Scoring.Compare(q.Expect, actual);
                exact = parts.Values.All(x => x);
            }
            // 延伸詞：去掉與原文關鍵字相同的，再到案件 LOG 查次數，有出現才當建議
            var expansions = parsed is JsonObject po && po["expansions"] is JsonArray ea
                ? ea.Select(x => x?.ToString() ?? "").Where(x => x.Trim().Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList() : new List<string>();
            var keywords = actual is null ? new HashSet<string>() : actual.AllOf.Concat(actual.AnyOf).Concat(actual.NoneOf).Select(Checks.Norm).ToHashSet();
            var suggestions = expansions.Where(x => !keywords.Contains(Checks.Norm(x)))
                .Select(x => (Term: x, Count: caseLog!.Count(x))).Where(x => x.Count > 0).OrderByDescending(x => x.Count).ToList();
            extra["hints"] = new JsonArray(hints.Select(h => (JsonNode)h).ToArray());
            extra["expansions"] = new JsonArray(expansions.Select(x => (JsonNode)x).ToArray());
            extra["suggestions"] = new JsonArray(suggestions.Select(x => (JsonNode)new JsonObject { ["term"] = x.Term, ["count"] = x.Count }).ToArray());
            if (q.ExpectSuggest is { } want)
            {
                var wantNorm = want.Select(Checks.Norm).ToHashSet();
                var good = suggestions.Count(x => wantNorm.Contains(Checks.Norm(x.Term)));
                extra["expectSuggest"] = new JsonArray(want.Select(x => (JsonNode)x).ToArray());
                extra["suggestHit"] = good > 0;
                extra["suggestGood"] = good;
            }
        }
        var decode = firstToken is { } ft && outTokens > 1 ? (outTokens - 1) / Math.Max(0.001, sw.Elapsed.TotalSeconds - ft) : 0;
        items.Add(new JsonObject
        {
            ["id"] = q.Id, ["exact"] = exact, ["valid"] = actual is not null, ["error"] = error,
            ["expectUnsupported"] = q.Expect.Unsupported, ["actualUnsupported"] = actual?.Unsupported,
            ["parts"] = new JsonObject(parts.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))),
            ["promptTokens"] = promptTokens, ["outputTokens"] = outTokens,
            ["firstTokenSeconds"] = firstToken ?? 0, ["totalSeconds"] = sw.Elapsed.TotalSeconds, ["decodeTokensPerSecond"] = decode,
            ["output"] = raw.Length > 600 ? raw[..600] : raw,
            ["llmInput"] = ext?.Remainder,
        });
        foreach (var kv in extra.ToList()) { extra.Remove(kv.Key); items[^1]!.AsObject()[kv.Key] = kv.Value; }
        Console.WriteLine($"  [{label}] {q.Id} {(exact ? "OK " : "NG ")} {sw.Elapsed.TotalSeconds,5:N1}s {raw.ReplaceLineEndings(" ")[..Math.Min(raw.Length, 120)]}");
    }
    runs.Add(new JsonObject { ["guided"] = guided, ["fewShot"] = fewShot, ["label"] = label, ["items"] = items });
}

using var self = Process.GetCurrentProcess();
self.Refresh();
var result = new JsonObject
{
    ["model"] = opts["name"], ["modelPath"] = Path.GetFileName(opts["model"].TrimEnd('\\', '/')), ["family"] = family.ToString(),
    ["device"] = "CPU", ["hybrid"] = hybrid, ["expand"] = expand, ["processorCount"] = Environment.ProcessorCount,
    ["loadSeconds"] = load.Elapsed.TotalSeconds, ["peakWorkingSetMB"] = self.PeakWorkingSet64 / (1024.0 * 1024),
    ["date"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), ["runs"] = runs,
};
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(opts["out"]))!);
File.WriteAllText(opts["out"], result.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
Console.WriteLine($"已寫入 {opts["out"]}；峰值記憶體 {self.PeakWorkingSet64 / (1024 * 1024):N0} MB");
return 0;

static class Args
{
    public static Dictionary<string, string> Parse(string[] args)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
            var key = args[i][2..];
            var values = new List<string>();
            while (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) values.Add(args[++i]);
            d[key] = string.Join(";", values);
        }
        return d;
    }
}
