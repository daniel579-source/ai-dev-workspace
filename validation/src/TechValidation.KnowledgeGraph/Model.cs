using LogTrace.Engine.Knowledge;
using LogTrace.Engine.Search;

namespace TechValidation.KnowledgeGraph;

/// <summary>情境預期找到的內容（人工標註的正解）。警報為「元素 id|代碼」，知識庫為「代碼|說明」。</summary>
public sealed record Expected(List<string> Kb, List<string> Devices, List<string> Alarms, List<string> Incidents, List<string> Safety, bool Stall, bool NoLog);

public sealed record MustNot(List<string> Alarms);

/// <summary>一個情境：第一線在某個時刻對某台設備提出的問題。</summary>
public sealed record Scenario(string Id, string Title, DateTime Time, string Device, string Question, Expected Expected, MustNot MustNot);

/// <summary>異常紀錄（ADR-0016 的簡化版，只取比對需要的欄位）；Kind 為 alarm 或 stall（ADR-0016 2026-11-16 補充的異常類型）。</summary>
public sealed record Incident(string Id, string Kind, List<string> AlarmCodes, List<string> Devices, string Symptom);

/// <summary>安全清單的一列（ADR-0014 第 5 點，候選格式）：關鍵字以 | 分隔；警報代碼可以省略。</summary>
public sealed record SafetyRow(string Category, string[] Keywords, string? Code, string Instruction, string Source);

public static class SafetyList
{
    public static List<SafetyRow> Load(string path) =>
        File.ReadAllLines(path).Skip(1).Where(l => l.Trim().Length > 0).Select(l => l.Split(','))
            .Select(p => new SafetyRow(p[0], p[1].Split('|', StringSplitOptions.RemoveEmptyEntries), p[2].Length == 0 ? null : p[2], p[3], p[4]))
            .ToList();

    /// <summary>警報代碼完全比對，或關鍵字出現在文字中（不分大小寫、全形半形，ADR-0010 的正規化）。</summary>
    public static IEnumerable<SafetyRow> Match(IEnumerable<SafetyRow> rows, IReadOnlySet<string> codes, IEnumerable<string> texts)
    {
        var normalized = texts.Select(TextNormalizer.Normalize).ToList();
        return rows.Where(r => (r.Code is { } c && codes.Contains(c))
            || r.Keywords.Any(k => normalized.Any(t => t.Contains(TextNormalizer.Normalize(k), StringComparison.Ordinal))));
    }
}

/// <summary>找到的內容。Events 為展開後要交給 LLM 的事件數（ADR-0015 第 3 點），null 表示這個方法不看事件。</summary>
public sealed record Found(
    IReadOnlySet<string> Kb, IReadOnlySet<string> Devices, IReadOnlySet<string>? Alarms, IReadOnlySet<string> Incidents,
    IReadOnlySet<string> Safety, bool? Stall, bool? NoLog, int? Events)
{
    public static string KbKey(KnowledgeEntry e) => $"{e.Code}|{e.Description}";
}
