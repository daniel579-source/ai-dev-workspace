using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TechValidation.LlmQuery;

/// <summary>
/// 搜尋條件（對應 LogTrace.Engine.Search.SearchQuery 的子集，ADR-0010）：關鍵字三框、時間區間、模組、等級、解析欄位。
/// Unsupported：問題不能轉成搜尋條件（問原因、處置等），模型不得編造條件（ADR-0013 第 5 點）。
/// </summary>
public sealed record Query(
    bool Unsupported,
    IReadOnlyList<string> AllOf,
    IReadOnlyList<string> AnyOf,
    IReadOnlyList<string> NoneOf,
    DateTime? From,
    DateTime? To,
    IReadOnlyList<string> Modules,
    IReadOnlyList<string> Levels,
    IReadOnlyList<(string Kind, string Value)> Fields)
{
    public static readonly Query UnsupportedQuery = new(true, [], [], [], null, null, [], [], []);

    /// <summary>解析模型或問題集的 JSON。格式錯誤時丟出 FormatException。</summary>
    public static Query Parse(JsonNode? node)
    {
        if (node is JsonValue v && v.TryGetValue<string>(out var s) && s == "unsupported") return UnsupportedQuery;
        if (node is not JsonObject o) throw new FormatException("不是 JSON 物件");
        if (o["unsupported"] is JsonValue u && u.TryGetValue<bool>(out var flag) && flag) return UnsupportedQuery;
        return new(false, Strings(o, "allOf"), Strings(o, "anyOf"), Strings(o, "noneOf"), Time(o, "from"), Time(o, "to"),
            Strings(o, "modules"), Strings(o, "levels"),
            o["fields"] is JsonArray fa
                ? fa.Select(f => f is JsonObject fo && fo["kind"]?.GetValue<string>() is { } k && fo["value"]?.ToString() is { } val
                    ? (k, val) : throw new FormatException("fields 的項目要有 kind 與 value")).ToList()
                : []);
    }

    static List<string> Strings(JsonObject o, string name) =>
        o[name] switch
        {
            null => [],
            JsonArray a => a.Select(x => x?.ToString() ?? "").Where(x => x.Trim().Length > 0).ToList(),
            JsonValue v when v.TryGetValue<string>(out var s) => s.Trim().Length > 0 ? [s] : [],
            _ => throw new FormatException($"{name} 要是字串陣列"),
        };

    static DateTime? Time(JsonObject o, string name)
    {
        if (o[name] is not JsonValue v || !v.TryGetValue<string>(out var s) || string.IsNullOrWhiteSpace(s)) return null;
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t
            : throw new FormatException($"{name} 的時間「{s}」格式不正確");
    }
}

/// <summary>評分：每個部分分開比對（關鍵字不分大小寫、順序不計；欄位種類與值不分大小寫）。</summary>
public static class Scoring
{
    public static readonly string[] Parts = ["unsupported", "allOf", "anyOf", "noneOf", "from", "to", "modules", "levels", "fields"];

    public static Dictionary<string, bool> Compare(Query expect, Query actual)
    {
        static bool SetEq(IEnumerable<string> a, IEnumerable<string> b) =>
            a.Select(Norm).ToHashSet().SetEquals(b.Select(Norm));
        static string Norm(string s) => s.Trim().ToUpperInvariant();
        return new()
        {
            ["unsupported"] = expect.Unsupported == actual.Unsupported,
            ["allOf"] = SetEq(expect.AllOf, actual.AllOf),
            ["anyOf"] = SetEq(expect.AnyOf, actual.AnyOf),
            ["noneOf"] = SetEq(expect.NoneOf, actual.NoneOf),
            ["from"] = expect.From == actual.From,
            ["to"] = expect.To == actual.To,
            ["modules"] = SetEq(expect.Modules, actual.Modules),
            ["levels"] = SetEq(expect.Levels, actual.Levels),
            ["fields"] = SetEq(expect.Fields.Select(f => f.Kind + "=" + f.Value), actual.Fields.Select(f => f.Kind + "=" + f.Value)),
        };
    }

    /// <summary>從模型的輸出擷取第一個完整的 JSON 物件（只用提示詞時，模型可能前後多說話）。</summary>
    public static string? ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        while (start >= 0)
        {
            int depth = 0;
            bool inString = false, escape = false;
            for (int i = start; i < text.Length; i++)
            {
                var c = text[i];
                if (inString)
                {
                    if (escape) escape = false;
                    else if (c == '\\') escape = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') inString = true;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0)
                {
                    var candidate = text[start..(i + 1)];
                    try
                    {
                        using var _ = JsonDocument.Parse(candidate);
                        return candidate;
                    }
                    catch (JsonException)
                    {
                        break;
                    }
                }
            }
            start = text.IndexOf('{', start + 1);
        }
        return null;
    }
}
