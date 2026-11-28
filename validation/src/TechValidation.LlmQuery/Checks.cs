using System.Text;
using System.Text.Json.Nodes;

namespace TechValidation.LlmQuery;

/// <summary>
/// #55 第三輪（使用者決定 2026-11-27）：以程式檢查 LLM 的輸出，延伸詞以案件 LOG 驗證後只當建議。
/// A：關鍵字必須在問題原文出現；B：模組與等級必須在問題原文出現；
/// C：關鍵字剛好是模組或等級名稱時不搬移，只記錄為提示（「也可能是等級／模組」）；
/// D：anyOf 只有一個字且 allOf 是空的時改為 allOf（搜尋結果相同，Jev 0.70）；
/// E（第四輪）：關鍵字與已填的模組或等級名稱相同時刪除；F（第五輪）：問法與一般用語的停用詞。
/// 比對方式與搜尋相同（ADR-0010）：不分大小寫、全半形相同；另外忽略空白。
/// </summary>
public static class Checks
{
    public static string Norm(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s.Normalize(NormalizationForm.FormKC))
            if (!char.IsWhiteSpace(c)) sb.Append(char.ToUpperInvariant(c));
        return sb.ToString();
    }

    /// <summary>
    /// F（第五輪，使用者決定 2026-11-28，Jev 0.91；比對方式 Jev 0.77）：問法與一般用語，關鍵字（正規化後）與其中之一完全相同時刪除。
    /// 來源只限：第三、四輪結果中實際被當成關鍵字的問法，以及提示詞已寫明不是關鍵字的一般用語（不取自第五輪的保留測試集）。
    /// </summary>
    public static readonly string[] StopWords =
    [
        "發生", "從", "從頭到尾", "去過哪裡", "變化", "被讀到", "讀到", "做了什麼", "做了哪些動作", "時間",
        "事件", "紀錄", "訊息", "經過", "過程", "什麼時候",
    ];
    static readonly HashSet<string> StopNorm = StopWords.Select(Norm).ToHashSet();

    public sealed record Result(Query Query, IReadOnlyList<string> Hints);

    public static Result Apply(Query q, string question, bool stopWords = true)
    {
        if (q.Unsupported) return new(q, []);
        var text = Norm(question);
        bool InText(string x) => Norm(x).Length > 0 && text.Contains(Norm(x), StringComparison.Ordinal);
        List<string> Keep(IEnumerable<string> xs) => xs.Where(InText).ToList();

        var allOf = Keep(q.AllOf);
        var anyOf = Keep(q.AnyOf);
        var noneOf = Keep(q.NoneOf);
        var modules = q.Modules.Where(InText).ToList();
        var levels = q.Levels.Where(InText).ToList();

        // E（第四輪，使用者決定 2026-11-27，Jev 0.74）：關鍵字與已填的模組或等級名稱相同時刪除（搜尋已經以模組、等級限制）
        if (stopWords) { allOf.RemoveAll(k => StopNorm.Contains(Norm(k))); anyOf.RemoveAll(k => StopNorm.Contains(Norm(k))); noneOf.RemoveAll(k => StopNorm.Contains(Norm(k))); }
        bool Dup(string k) => modules.Concat(levels).Any(x => Norm(x) == Norm(k));
        allOf.RemoveAll(Dup); anyOf.RemoveAll(Dup); noneOf.RemoveAll(Dup);

        var hints = new List<string>();
        foreach (var k in allOf.Concat(anyOf).Concat(noneOf))
        {
            if (Prompt.Modules.Any(m => Norm(m) == Norm(k)) && !modules.Any(m => Norm(m) == Norm(k))) hints.Add($"module:{k}");
            if (Prompt.Levels.Any(l => Norm(l) == Norm(k)) && !levels.Any(l => Norm(l) == Norm(k))) hints.Add($"level:{k}");
        }

        if (anyOf.Count == 1 && allOf.Count == 0) (allOf, anyOf) = (anyOf, []);
        return new(q with { AllOf = allOf, AnyOf = anyOf, NoneOf = noneOf, Modules = modules, Levels = levels }, hints);
    }
}

/// <summary>虛構的案件 LOG（訊息與筆數），模擬案件資料庫的「包含這個字的事件有幾筆」。</summary>
public sealed class CaseLog
{
    readonly List<(string Norm, int Count)> _messages;

    public CaseLog(string path)
    {
        var doc = JsonNode.Parse(File.ReadAllText(path))!;
        _messages = doc["messages"]!.AsArray()
            .Select(m => (Checks.Norm(m!["message"]!.GetValue<string>()), m["count"]!.GetValue<int>())).ToList();
    }

    /// <summary>部分比對（使用者決定 2026-11-27，與搜尋相同）。</summary>
    public int Count(string term)
    {
        var t = Checks.Norm(term);
        return t.Length == 0 ? 0 : _messages.Where(m => m.Norm.Contains(t, StringComparison.Ordinal)).Sum(m => m.Count);
    }
}
