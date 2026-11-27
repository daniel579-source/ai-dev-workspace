using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace TechValidation.LlmQuery;

/// <summary>把各模型的結果 JSON 整理成 Markdown 報告（只含統計與虛構題目的編號，沒有真實資料）。</summary>
public static class Report
{
    public static void Write(IReadOnlyList<string> files, string reportPath, string? note = null)
    {
        var results = files.Select(f => JsonNode.Parse(File.ReadAllText(f))!).ToList();
        var sb = new StringBuilder();
        static string P(double v) => (v * 100).ToString("N0", CultureInfo.InvariantCulture) + "%";
        static string N(double v, int d = 1) => v.ToString("N" + d, CultureInfo.InvariantCulture);
        static double Median(IEnumerable<double> xs)
        {
            var a = xs.OrderBy(x => x).ToArray();
            return a.Length == 0 ? 0 : a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
        }

        sb.AppendLine("# 本機 LLM：自然語言轉搜尋條件（#55，ADR-0013）");
        sb.AppendLine();
        var first = results[0];
        var firstItems = first["runs"]![0]!["items"]!.AsArray();
        int nSup = firstItems.Count(i => !i!["expectUnsupported"]!.GetValue<bool>()), nUns = firstItems.Count - nSup;
        var hybrid = first["hybrid"]?.GetValue<bool>() == true;
        var expand = first["expand"]?.GetValue<bool>() == true;
        sb.AppendLine($"- 量測：{first["date"]}，開發電腦（{first["processorCount"]} 執行緒），裝置 {first["device"]}，ONNX Runtime GenAI 0.20.0，貪婪解碼。");
        sb.AppendLine($"- 題目：虛構；可轉換 {nSup} 題、不能轉換（應回答 unsupported）{nUns} 題；有範例時附 {first["examples"]?.GetValue<int>() ?? (expand ? 7 : hybrid ? 6 : 4)} 個不在測試題中的範例（`llm-query/questions.json`）。");
        if (note is not null) sb.AppendLine($"- **注意**：{note}");
        if (expand) sb.AppendLine("- 第三輪：LLM 另外輸出延伸詞；「完全正確」為程式檢查後（關鍵字、模組、等級必須在問題原文出現；單一 anyOf 改為 allOf），括號內另列檢查前；延伸詞到虛構案件 LOG（`llm-query/case-log.json`）部分比對，有出現才算建議。");
        if (hybrid) sb.AppendLine("- 混合做法：時間與設備編號由規則抽出並換成佔位符，LLM 只處理關鍵字、模組、等級與 unsupported；from、to、fields 是規則的結果（模型回答 unsupported 時整題算 unsupported）。");
        sb.AppendLine("- 「完全正確」：關鍵字三框、時間起訖、模組、等級、解析欄位、unsupported 全部與預期相同（關鍵字不分大小寫、不計順序）。");
        sb.AppendLine();
        sb.AppendLine("## 正確率");
        sb.AppendLine();
        sb.AppendLine($"| 模型 | 組合 | 完全正確（可轉換 {nSup}） | 不能轉換時回答 unsupported（{nUns}） | 可轉換卻回答 unsupported | 輸出不是有效 JSON | " + string.Join(" | ", Scoring.Parts.Skip(1)) + " |");
        sb.AppendLine("|---|---|---|---|---|---|" + string.Concat(Enumerable.Repeat("---|", Scoring.Parts.Length - 1)));
        foreach (var r in results)
            foreach (var run in r["runs"]!.AsArray())
            {
                var items = run!["items"]!.AsArray().Select(i => i!).ToList();
                var sup = items.Where(i => !i["expectUnsupported"]!.GetValue<bool>()).ToList();
                var uns = items.Where(i => i["expectUnsupported"]!.GetValue<bool>()).ToList();
                double Rate(IEnumerable<JsonNode> xs, Func<JsonNode, bool> f) { var l = xs.ToList(); return l.Count == 0 ? 0 : (double)l.Count(f) / l.Count; }
                var parts = Scoring.Parts.Skip(1).Select(p => P(Rate(sup, i => i["parts"]![p]!.GetValue<bool>())));
                var rawNote = expand ? $"；檢查前 {sup.Count(i => i["exactRaw"]!.GetValue<bool>())}" : "";
                sb.AppendLine($"| {r["model"]} | {run["label"]} | {P(Rate(sup, i => i["exact"]!.GetValue<bool>()))}（{sup.Count(i => i["exact"]!.GetValue<bool>())}{rawNote}） | " +
                    $"{uns.Count(i => i["actualUnsupported"]?.GetValue<bool>() == true)}／{uns.Count} | " +
                    $"{sup.Count(i => i["actualUnsupported"]?.GetValue<bool>() == true)} | {items.Count(i => !i["valid"]!.GetValue<bool>())} | {string.Join(" | ", parts)} |");
            }
        sb.AppendLine();
        sb.AppendLine($"各部分的比例只算可轉換的 {nSup} 題。");
        sb.AppendLine();
        if (expand)
        {
            sb.AppendLine("## 延伸詞建議");
            sb.AppendLine();
            sb.AppendLine("| 模型 | 組合 | 有預期用語的題目：建議中有正確用語 | 建議中正確的比例 | 每題平均建議數（有預期用語的題目） | 每題平均建議數（其他題目） | 延伸詞被案件 LOG 擋掉的比例 | 模組／等級提示次數 |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|");
            foreach (var r in results)
                foreach (var run in r["runs"]!.AsArray())
                {
                    var items = run!["items"]!.AsArray().Select(i => i!).ToList();
                    var withWant = items.Where(i => i["expectSuggest"] is not null).ToList();
                    var others = items.Where(i => i["expectSuggest"] is null && !i["expectUnsupported"]!.GetValue<bool>()).ToList();
                    int Sug(JsonNode i) => i["suggestions"]!.AsArray().Count;
                    int totalSug = withWant.Sum(Sug), good = withWant.Sum(i => i["suggestGood"]!.GetValue<int>());
                    int exp = items.Sum(i => i["expansions"]!.AsArray().Count), shown = items.Sum(Sug);
                    sb.AppendLine($"| {r["model"]} | {run["label"]} | {(withWant.Count == 0 ? "－" : $"{withWant.Count(i => i["suggestHit"]!.GetValue<bool>())}／{withWant.Count}")} | " +
                        $"{(totalSug == 0 ? "－" : P((double)good / totalSug))} | {(withWant.Count == 0 ? "－" : N((double)totalSug / withWant.Count))} | " +
                        $"{(others.Count == 0 ? "－" : N((double)others.Sum(Sug) / others.Count))} | {(exp == 0 ? "－" : P((double)(exp - shown) / exp))} | {items.Sum(i => i["hints"]!.AsArray().Count)} |");
                }
            sb.AppendLine();
            sb.AppendLine("延伸詞被擋掉：模型提出、但案件 LOG 中沒有出現（或與原文關鍵字相同）的字，不顯示給工程師。");
            sb.AppendLine();
        }
        sb.AppendLine("## 速度與記憶體");
        sb.AppendLine();
        sb.AppendLine("| 模型 | 載入（秒） | 峰值記憶體（MB） | 組合 | 提示詞 token（中位數） | 第一個 token（秒，中位數） | 每題總時間（秒，中位數／最大） | 產生速度（token/秒，中位數） |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var r in results)
            foreach (var run in r["runs"]!.AsArray())
            {
                var items = run!["items"]!.AsArray().Select(i => i!).ToList();
                sb.AppendLine($"| {r["model"]} | {N(r["loadSeconds"]!.GetValue<double>())} | {N(r["peakWorkingSetMB"]!.GetValue<double>(), 0)} | {run["label"]} | " +
                    $"{N(Median(items.Select(i => i["promptTokens"]!.GetValue<double>())), 0)} | {N(Median(items.Select(i => i["firstTokenSeconds"]!.GetValue<double>())), 2)} | " +
                    $"{N(Median(items.Select(i => i["totalSeconds"]!.GetValue<double>())))}／{N(items.Max(i => i["totalSeconds"]!.GetValue<double>()))} | " +
                    $"{N(Median(items.Select(i => i["decodeTokensPerSecond"]!.GetValue<double>())))} |");
            }
        sb.AppendLine();
        sb.AppendLine("## 錯誤的題目");
        sb.AppendLine();
        foreach (var r in results)
            foreach (var run in r["runs"]!.AsArray())
            {
                var wrong = run!["items"]!.AsArray().Where(i => !i!["exact"]!.GetValue<bool>() && !(i["expectUnsupported"]!.GetValue<bool>() && i["actualUnsupported"]?.GetValue<bool>() == true))
                    .Select(i => $"{i!["id"]}（{(i["valid"]!.GetValue<bool>() ? string.Join("、", i["parts"]!.AsObject().Where(p => !p.Value!.GetValue<bool>()).Select(p => p.Key)) : "無效 JSON")}）");
                sb.AppendLine($"- **{r["model"]}／{run["label"]}**：{string.Join("；", wrong).DefaultIfEmpty("無")}");
            }
        File.WriteAllText(reportPath, sb.ToString());
        Console.WriteLine($"已寫入 {reportPath}");
    }

    static string DefaultIfEmpty(this string s, string fallback) => s.Length == 0 ? fallback : s;
}
