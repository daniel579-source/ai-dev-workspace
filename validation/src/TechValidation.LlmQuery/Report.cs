using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace TechValidation.LlmQuery;

/// <summary>把各模型的結果 JSON 整理成 Markdown 報告（只含統計與虛構題目的編號，沒有真實資料）。</summary>
public static class Report
{
    public static void Write(IReadOnlyList<string> files, string reportPath)
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
        sb.AppendLine($"- 量測：{first["date"]}，開發電腦（{first["processorCount"]} 執行緒），裝置 {first["device"]}，ONNX Runtime GenAI 0.20.0，貪婪解碼。");
        sb.AppendLine("- 題目：`llm-query/questions.json`（虛構）；可轉換 50 題、不能轉換（應回答 unsupported）6 題；有範例時附 4 個不在測試題中的範例。");
        sb.AppendLine("- 「完全正確」：關鍵字三框、時間起訖、模組、等級、解析欄位、unsupported 全部與預期相同（關鍵字不分大小寫、不計順序）。");
        sb.AppendLine();
        sb.AppendLine("## 正確率");
        sb.AppendLine();
        sb.AppendLine("| 模型 | 組合 | 完全正確（可轉換 50） | 不能轉換時回答 unsupported（6） | 可轉換卻回答 unsupported | 輸出不是有效 JSON | " + string.Join(" | ", Scoring.Parts.Skip(1)) + " |");
        sb.AppendLine("|---|---|---|---|---|---|" + string.Concat(Enumerable.Repeat("---|", Scoring.Parts.Length - 1)));
        foreach (var r in results)
            foreach (var run in r["runs"]!.AsArray())
            {
                var items = run!["items"]!.AsArray().Select(i => i!).ToList();
                var sup = items.Where(i => !i["expectUnsupported"]!.GetValue<bool>()).ToList();
                var uns = items.Where(i => i["expectUnsupported"]!.GetValue<bool>()).ToList();
                double Rate(IEnumerable<JsonNode> xs, Func<JsonNode, bool> f) { var l = xs.ToList(); return l.Count == 0 ? 0 : (double)l.Count(f) / l.Count; }
                var parts = Scoring.Parts.Skip(1).Select(p => P(Rate(sup, i => i["parts"]![p]!.GetValue<bool>())));
                sb.AppendLine($"| {r["model"]} | {run["label"]} | {P(Rate(sup, i => i["exact"]!.GetValue<bool>()))}（{sup.Count(i => i["exact"]!.GetValue<bool>())}） | " +
                    $"{uns.Count(i => i["actualUnsupported"]?.GetValue<bool>() == true)}／{uns.Count} | " +
                    $"{sup.Count(i => i["actualUnsupported"]?.GetValue<bool>() == true)} | {items.Count(i => !i["valid"]!.GetValue<bool>())} | {string.Join(" | ", parts)} |");
            }
        sb.AppendLine();
        sb.AppendLine("各部分的比例只算可轉換的 50 題。");
        sb.AppendLine();
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
