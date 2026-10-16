using LogTrace.Engine.Events;

namespace LogTrace.Engine.Search;

/// <summary>
/// 搜尋條件（ADR-0010 第 2 點）。三個輸入框：全部包含（AND）、任一包含（OR）、不包含（NOT）；
/// 同一框中的關鍵字以空白分隔，包含空白的片語用雙引號括起來。所有條件之間為 AND。
/// </summary>
public sealed record SearchQuery
{
    public string AllOf { get; init; } = "";
    public string AnyOf { get; init; } = "";
    public string NoneOf { get; init; } = "";

    /// <summary>區分大小寫（全形與半形仍視為相同）。</summary>
    public bool CaseSensitive { get; init; }

    /// <summary>時間區間（含起點、不含終點）；null 表示不限。</summary>
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }

    public IReadOnlyList<string> Modules { get; init; } = [];
    public IReadOnlyList<string> Loggers { get; init; } = [];
    public IReadOnlyList<string> Levels { get; init; } = [];

    /// <summary>有任一個指定旗標的事件；None 表示不限。</summary>
    public EventFlags Flags { get; init; }

    /// <summary>解析欄位：FieldKinds 中任一種的值等於 FieldValues 中任一值（不分大小寫）。</summary>
    public IReadOnlyList<string> FieldKinds { get; init; } = [];
    public IReadOnlyList<string> FieldValues { get; init; } = [];

    public IReadOnlyList<string> AllTerms => Terms(AllOf);
    public IReadOnlyList<string> AnyTerms => Terms(AnyOf);
    public IReadOnlyList<string> NoneTerms => Terms(NoneOf);

    /// <summary>醒目標示用的關鍵字（全部包含與任一包含）。</summary>
    public IReadOnlyList<string> HighlightTerms => [.. AllTerms, .. AnyTerms];

    public bool HasText => AllTerms.Count + AnyTerms.Count + NoneTerms.Count > 0;

    /// <summary>以空白分隔；雙引號括起來的片語視為一個關鍵字。</summary>
    public static IReadOnlyList<string> Terms(string input)
    {
        var list = new List<string>();
        int i = 0;
        while (i < input.Length)
        {
            if (char.IsWhiteSpace(input[i])) { i++; continue; }
            if (input[i] is '"' or '＂' or '“' or '”')
            {
                int end = input.IndexOfAny(['"', '＂', '“', '”'], i + 1);
                if (end < 0) end = input.Length;
                var phrase = input[(i + 1)..end];
                if (phrase.Trim().Length > 0) list.Add(phrase);
                i = end + 1;
                continue;
            }
            int j = i;
            while (j < input.Length && !char.IsWhiteSpace(input[j])) j++;
            list.Add(input[i..j]);
            i = j;
        }
        return list;
    }

    /// <summary>以正規化後的文字判斷是否符合文字條件（區分大小寫時，訊息與關鍵字都不做大小寫摺疊）。</summary>
    public bool MatchesText(string message)
    {
        var text = TextNormalizer.Normalize(message, CaseSensitive);
        bool Has(string term) => text.Contains(TextNormalizer.Normalize(term, CaseSensitive), StringComparison.Ordinal);
        return AllTerms.All(Has) && (AnyTerms.Count == 0 || AnyTerms.Any(Has)) && !NoneTerms.Any(Has);
    }

    /// <summary>訊息中符合關鍵字的位置（原文的起點與長度，依位置排序、合併重疊）。</summary>
    public IReadOnlyList<(int Start, int Length)> FindMatches(string message)
    {
        var (text, start, length) = TextNormalizer.NormalizeWithMap(message, CaseSensitive);
        var spans = new List<(int Start, int End)>();
        foreach (var term in HighlightTerms)
        {
            var t = TextNormalizer.Normalize(term, CaseSensitive);
            if (t.Length == 0) continue;
            for (int i = text.IndexOf(t, StringComparison.Ordinal); i >= 0; i = text.IndexOf(t, i + 1, StringComparison.Ordinal))
            {
                int s = start[i], e = start[i + t.Length - 1] + length[i + t.Length - 1];
                spans.Add((s, e));
            }
        }
        var merged = new List<(int Start, int Length)>();
        foreach (var (s, e) in spans.OrderBy(x => x.Start))
        {
            if (merged.Count > 0 && s <= merged[^1].Start + merged[^1].Length)
            {
                var last = merged[^1];
                merged[^1] = (last.Start, Math.Max(last.Start + last.Length, e) - last.Start);
            }
            else merged.Add((s, e - s));
        }
        return merged;
    }
}
