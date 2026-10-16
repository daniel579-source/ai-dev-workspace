using System.Text.RegularExpressions;
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

    /// <summary>
    /// 正規表示式（ADR-0010 第 2 點，進階選項）：三個輸入框中的每個關鍵字都是一個 .NET 正規表示式。
    /// 比對 NFKC 正規化後的整筆事件內容（全形與半形相同）；不區分大小寫時加上 IgnoreCase。一律以掃描執行。
    /// </summary>
    public bool Regex { get; init; }

    /// <summary>單一事件比對正規表示式的時間上限；超過時停止搜尋並說明（避免寫錯的正規表示式讓畫面停住）。</summary>
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    // 編譯好的正規表示式依查詢物件保存（不放在 record 的欄位中，避免影響相等比較與 with 複製）
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SearchQuery, Regex[][]> Compiled = new();

    (Regex[] All, Regex[] Any, Regex[] None) CompileRegex()
    {
        var c = Compiled.GetValue(this, q =>
        {
            var options = RegexOptions.CultureInvariant | (q.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
            Regex[] Compile(IReadOnlyList<string> terms) =>
                terms.Select(t => new Regex(TextNormalizer.Normalize(t, caseSensitive: true), options, RegexTimeout)).ToArray();
            return [Compile(q.AllTerms), Compile(q.AnyTerms), Compile(q.NoneTerms)];
        });
        return (c[0], c[1], c[2]);
    }

    /// <summary>檢查條件；正規表示式的語法有誤時傳回說明，否則為 null。</summary>
    public string? Validate()
    {
        if (!Regex) return null;
        foreach (var t in AllTerms.Concat(AnyTerms).Concat(NoneTerms))
        {
            try
            {
                _ = new Regex(TextNormalizer.Normalize(t, caseSensitive: true), RegexOptions.CultureInvariant, RegexTimeout);
            }
            catch (ArgumentException ex)
            {
                return $"正規表示式「{t}」有誤：{ex.Message}";
            }
        }
        return null;
    }

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
        if (Regex)
        {
            var (all, any, none) = CompileRegex();
            var n = TextNormalizer.Normalize(message, caseSensitive: true);
            return all.All(r => r.IsMatch(n)) && (any.Length == 0 || any.Any(r => r.IsMatch(n))) && !none.Any(r => r.IsMatch(n));
        }
        var text = TextNormalizer.Normalize(message, CaseSensitive);
        bool Has(string term) => text.Contains(TextNormalizer.Normalize(term, CaseSensitive), StringComparison.Ordinal);
        return AllTerms.All(Has) && (AnyTerms.Count == 0 || AnyTerms.Any(Has)) && !NoneTerms.Any(Has);
    }

    /// <summary>訊息中符合關鍵字的位置（原文的起點與長度，依位置排序、合併重疊）。</summary>
    public IReadOnlyList<(int Start, int Length)> FindMatches(string message)
    {
        var (text, start, length) = TextNormalizer.NormalizeWithMap(message, Regex || CaseSensitive);
        var spans = new List<(int Start, int End)>();
        if (Regex)
        {
            var (all, any, _) = CompileRegex();
            foreach (var r in all.Concat(any))
                foreach (Match m in r.Matches(text))
                    if (m.Length > 0) spans.Add((start[m.Index], start[m.Index + m.Length - 1] + length[m.Index + m.Length - 1]));
        }
        else foreach (var term in HighlightTerms)
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
