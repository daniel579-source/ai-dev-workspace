using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using LogTrace.Engine.Events;
using LogTrace.Engine.Parsing;
using LogTrace.Engine.Search;

namespace LogTrace.Workspace;

/// <summary>一段文字，以及是否為搜尋符合處（醒目標示）。</summary>
public sealed record TextSegment(string Text, bool IsMatch);

/// <summary>事件清單的一列。</summary>
/// <param name="moduleIndex">模組在案件模組順序中的位置；混合檢視以它決定底色。</param>
/// <param name="masker">個人資料的遮罩（ADR-0009）；顯示的訊息一律遮罩。</param>
public sealed partial class EventRowViewModel(LogEvent e, int moduleIndex = 0, TextMasker? masker = null) : ObservableObject
{
    readonly TextMasker _masker = masker ?? TextMasker.None;

    public LogEvent Event { get; } = e;

    public long Sequence => Event.Sequence;
    public string Time => Event.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.ffff", CultureInfo.InvariantCulture);
    public string Module => Event.Module;
    public int ModuleIndex { get; } = moduleIndex;
    public string Logger => Event.Logger;
    public string Level => Event.Level;

    public IReadOnlyList<FlagInfo> Flags { get; } = FlagInfo.Of(e.Flags);

    /// <summary>旗標圖示（依圖例順序）。</summary>
    public string FlagGlyphs => string.Concat(Flags.Select(f => f.Glyph));

    /// <summary>滑鼠移到旗標上時的說明；沒有旗標時為 null（不顯示）。</summary>
    public string? FlagToolTip => Flags.Count == 0 ? null : string.Join("\n", Flags.Select(f => $"{f.Glyph} {f.Name}：{f.Description}"));

    /// <summary>顯示用的完整訊息：編碼錯誤以替代字元顯示、個人資料遮罩。長度與原文相同。</summary>
    public string DisplayMessage => _masker.Mask(DisplayText(Event.Message));

    /// <summary>清單只顯示第一行；延續行在詳細資訊中顯示。</summary>
    public string Message
    {
        get
        {
            var text = DisplayMessage;
            var i = text.IndexOf('\n');
            return i < 0 ? text : text[..i] + " …";
        }
    }

    /// <summary>分軌檢視：每個模組一欄，只在事件所屬模組的欄位顯示訊息。</summary>
    public string this[string module] => string.Equals(module, Module, StringComparison.Ordinal) ? Message : "";

    /// <summary>原始位置，例如「20300107/PLC-2030-01-07/PLCLOG.txt 第 3～5 行」。</summary>
    public string Source => FormatSource(Event.Source);

    // ---- 搜尋（ADR-0010 第 4 點） ----

    /// <summary>符合目前的搜尋條件（「顯示全部並標示符合的」模式用）。</summary>
    [ObservableProperty]
    public partial bool IsMatch { get; set; }

    /// <summary>關鍵字在訊息中的位置（原文的起點與長度）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MessageSegments), nameof(MatchNote))]
    public partial IReadOnlyList<(int Start, int Length)> Highlights { get; set; } = [];

    /// <summary>清單中第一行的文字，分成一般與醒目的片段。</summary>
    public IReadOnlyList<TextSegment> MessageSegments
    {
        get
        {
            var text = Message;
            int firstLine = DisplayMessage.IndexOf('\n') is var nl and >= 0 ? nl : DisplayMessage.Length;
            return Segments(text, Highlights.Where(h => h.Start < firstLine).Select(h => (h.Start, Math.Min(h.Length, firstLine - h.Start))).ToList());
        }
    }

    /// <summary>符合處只在延續行時：「符合處在第 N 行」（原始檔的行號）。</summary>
    public string? MatchNote
    {
        get
        {
            if (Highlights.Count == 0) return null;
            var lines = Highlights.Select(h => Event.Message[..h.Start].Count(c => c == '\n')).Distinct().ToList();
            if (lines.Contains(0)) return null;
            return "符合處在第 " + string.Join("、", lines.Select(l => (Event.Source.LineStart + l).ToString(CultureInfo.InvariantCulture))) + " 行";
        }
    }

    public static IReadOnlyList<TextSegment> Segments(string text, IReadOnlyList<(int Start, int Length)> highlights)
    {
        var list = new List<TextSegment>();
        int pos = 0;
        foreach (var (start, length) in highlights.Where(h => h.Start < text.Length).OrderBy(h => h.Start))
        {
            if (start < pos) continue;
            if (start > pos) list.Add(new TextSegment(text[pos..start], false));
            int end = Math.Min(text.Length, start + length);
            list.Add(new TextSegment(text[start..end], true));
            pos = end;
        }
        if (pos < text.Length) list.Add(new TextSegment(text[pos..], false));
        return list;
    }

    public static string FormatSource(EventSource s) =>
        s.LineStart == s.LineEnd
            ? $"{s.RelativePath} 第 {s.LineStart} 行"
            : $"{s.RelativePath} 第 {s.LineStart}～{s.LineEnd} 行";

    /// <summary>
    /// 畫面顯示用：編碼錯誤以替代字元顯示（ADR-0004 第 6 點）。Big5 解碼器產生的私用區字元也換成替代字元；
    /// 儲存的內容與原始檔不受影響。長度不變。
    /// </summary>
    public static string DisplayText(string text)
    {
        if (!LogEncoding.HasDecodingError(text)) return text;
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text) sb.Append(ch is >= '\uE000' and <= '\uF8FF' ? '\uFFFD' : ch);
        return sb.ToString();
    }
}
