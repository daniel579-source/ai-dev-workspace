using System.Globalization;
using System.Text.RegularExpressions;

namespace TechValidation.LlmQuery;

/// <summary>
/// 混合做法（#55 第二輪，使用者決定 2026-11-27）：時間與設備編號先由規則抽出，
/// 抽到的片段在問題中換成佔位符，LLM 只看剩下的文字，處理關鍵字、模組、等級與 unsupported。
/// 編號格式是虛構的範例格式（與 Prompt.Kinds 的說明相同）；正式產品中各廠的編號格式來自廠別設定（待確認能否重用於問題文字）。
/// </summary>
public static partial class Rules
{
    public const string TimeMark = "〔時間〕";
    public const string IdMark = "〔編號〕";

    public sealed record Extracted(DateTime? From, DateTime? To, IReadOnlyList<(string Kind, string Value)> Fields, string Remainder);

    // 編號前後不能緊接英數字（中文字在 .NET 也算 \w，所以不用 \b）
    const string L = "(?<![A-Za-z0-9])";
    const string R = "(?![A-Za-z0-9])";

    static readonly (string Kind, Regex Pattern)[] IdPatterns =
    [
        ("conv", new($@"(?:Conv|輸送設備)\s*(?<v>[A-Z]-\d{{1,3}}|\d{{3,4}}){R}", RegexOptions.IgnoreCase)),
        ("vehicle", new($@"(?:搬送機\s*)?{L}(?<v>S[SR]CA\d{{2}}){R}", RegexOptions.IgnoreCase)),
        ("rm", new($@"{L}(?<v>RM\d{{2}}){R}", RegexOptions.IgnoreCase)),
        ("elevator", new($@"(?:電梯\s*)?{L}(?<v>EV\d{{2}}){R}", RegexOptions.IgnoreCase)),
        ("eq", new($@"{L}EQ\s*(?<v>\d{{4}}){R}", RegexOptions.IgnoreCase)),
        ("fireDoor", new($@"(?:防火門\s*)?{L}(?<v>FD\d{{2}}){R}", RegexOptions.IgnoreCase)),
        ("command", new($@"(?:命令\s*)?{L}(?<v>CMD-\d{{4}}){R}", RegexOptions.IgnoreCase)),
        ("pallet", new($@"(?:棧板\s*)?{L}(?<v>PLT\d{{5}}){R}", RegexOptions.IgnoreCase)),
        ("bin", new($@"(?:桶\s*)?{L}(?<v>BIN\d{{4}}){R}", RegexOptions.IgnoreCase)),
        ("storage", new($@"(?:儲位\s*)?(?<!\d)(?<v>\d{{7}})(?!\d)")),
        ("signal", new($@"(?:訊號\s*)?{L}(?<v>X\d{{4}}){R}", RegexOptions.IgnoreCase)),
        ("alarmCode", new($@"(?:警報代碼\s*)?{L}(?<v>E-\d{{3}}){R}(?:\s*警報)?", RegexOptions.IgnoreCase)),
        ("bcr", new($@"(?:條碼讀取器\s*)?{L}(?<v>BCR\d{{2}}){R}", RegexOptions.IgnoreCase)),
        ("barcode", new($@"(?:條碼\s*)?{L}(?<v>BC\d{{6}}){R}", RegexOptions.IgnoreCase)),
        ("scale", new($@"(?:磅秤\s*)?{L}(?<v>SC\d{{2}}){R}", RegexOptions.IgnoreCase)),
    ];

    const string Point = @"(?<p{0}>上午|早上|下午|晚上|中午|凌晨)?\s*(?<![\dA-Za-z-])(?:(?<h{0}>\d{{1,2}}):(?<m{0}>\d{{2}})|(?<h{0}>\d{{1,2}})\s*點\s*(?:(?<m{0}>\d{{1,2}})\s*分|(?<half{0}>半)|整)?)";
    static readonly Regex Range = new(string.Format(CultureInfo.InvariantCulture, Point, 1) + @"\s*(?:到|至|~|～)\s*" + string.Format(CultureInfo.InvariantCulture, Point, 2) + @"(?:\s*之間)?");
    static readonly Regex After = new(string.Format(CultureInfo.InvariantCulture, Point, 1) + @"\s*(?:以後|之後)");
    static readonly Regex Before = new(string.Format(CultureInfo.InvariantCulture, Point, 1) + @"\s*(?:以前|之前)");

    public static Extracted Extract(string question, string caseDate)
    {
        var date = DateTime.ParseExact(caseDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var fields = new List<(int Index, string Kind, string Value)>();
        var text = question;
        foreach (var (kind, pattern) in IdPatterns)
        {
            text = pattern.Replace(text, m =>
            {
                fields.Add((m.Index, kind, m.Groups["v"].Value.ToUpperInvariant()));
                return IdMark;
            });
        }

        DateTime? from = null, to = null;
        text = Range.Replace(text, m =>
        {
            var a = Time(m, 1, date, null);
            var b = Time(m, 2, date, m.Groups["p1"].Success ? m.Groups["p1"].Value : null);
            if (a is null || b is null) return m.Value;
            (from, to) = (a, b);
            return TimeMark;
        }, 1);
        if (from is null && to is null)
        {
            text = After.Replace(text, m => Time(m, 1, date, null) is { } t ? Mark(ref from, t) : m.Value, 1);
            text = Before.Replace(text, m => Time(m, 1, date, null) is { } t ? Mark(ref to, t) : m.Value, 1);
        }
        return new(from, to, fields.OrderBy(f => f.Index).Select(f => (f.Kind, f.Value)).Distinct().ToList(), text);
    }

    static string Mark(ref DateTime? slot, DateTime t)
    {
        slot = t;
        return TimeMark;
    }

    /// <summary>把「下午 3 點半」這類時間轉成案件日期的時間；第二個時間沒寫時段時沿用第一個。</summary>
    static DateTime? Time(Match m, int n, DateTime date, string? inheritedPeriod)
    {
        var period = m.Groups["p" + n].Success ? m.Groups["p" + n].Value : inheritedPeriod;
        var h = int.Parse(m.Groups["h" + n].Value, CultureInfo.InvariantCulture);
        var min = m.Groups["m" + n].Success ? int.Parse(m.Groups["m" + n].Value, CultureInfo.InvariantCulture)
            : m.Groups["half" + n].Success ? 30 : 0;
        if (period is "下午" or "晚上" && h < 12) h += 12;
        if (period is "凌晨" && h == 12) h = 0;
        if (h > 23 || min > 59) return null;
        return date.AddHours(h).AddMinutes(min);
    }
}
