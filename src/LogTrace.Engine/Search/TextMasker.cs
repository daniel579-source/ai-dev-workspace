using System.Text;
using System.Text.RegularExpressions;

namespace LogTrace.Engine.Search;

/// <summary>
/// 個人資料的遮罩（ADR-0009 第 1 點）：只影響顯示，原始 LOG 與搜尋用文字不變。
/// 符合遮罩規則的部分，保留前兩個字元與分隔符號，其餘的數字顯示為 x（例如 09xx-xxx-xxx）。
/// 遮罩後長度不變，醒目標示的位置仍然正確。
/// </summary>
public sealed class TextMasker
{
    /// <summary>初始規則：台灣手機號碼（09 開頭、10 位數，可以有連字號）；規則放在廠別設定中。</summary>
    public static IReadOnlyList<string> DefaultPatterns { get; } = [@"(?<![0-9])09[0-9]{2}-?[0-9]{3}-?[0-9]{3}(?![0-9])"];

    readonly List<Regex> _patterns;

    public TextMasker(IEnumerable<string> patterns) =>
        _patterns = patterns.Select(p => new Regex(p, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200))).ToList();

    public static TextMasker None { get; } = new([]);

    /// <summary>檢查遮罩規則；有問題時傳回說明。</summary>
    public static string? Check(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.CultureInvariant);
            return null;
        }
        catch (ArgumentException ex)
        {
            return $"遮罩規則「{pattern}」的正規表示式有誤：{ex.Message}";
        }
    }

    /// <summary>需要遮罩的位置（原文的起點與長度）。</summary>
    public IReadOnlyList<(int Start, int Length)> Find(string text)
    {
        var spans = new List<(int, int)>();
        foreach (var regex in _patterns)
        {
            try
            {
                foreach (Match m in regex.Matches(text)) if (m.Length > 0) spans.Add((m.Index, m.Length));
            }
            catch (RegexMatchTimeoutException)
            {
                // 規則太慢時不遮罩這一段
            }
        }
        return spans;
    }

    public bool HasMatches(string text) => Find(text).Count > 0;

    public string Mask(string text)
    {
        var spans = Find(text);
        if (spans.Count == 0) return text;
        var sb = new StringBuilder(text);
        foreach (var (start, length) in spans)
        {
            int kept = 0;
            for (int i = start; i < start + length; i++)
            {
                if (!char.IsAsciiDigit(sb[i])) continue;
                if (kept++ < 2) continue;
                sb[i] = 'x';
            }
        }
        return sb.ToString();
    }
}
