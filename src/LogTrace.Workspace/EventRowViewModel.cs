using System.Globalization;
using System.Text;
using LogTrace.Engine.Events;
using LogTrace.Engine.Parsing;

namespace LogTrace.Workspace;

/// <summary>事件清單的一列。</summary>
/// <param name="moduleIndex">模組在案件模組順序中的位置；混合檢視以它決定底色。</param>
public sealed class EventRowViewModel(LogEvent e, int moduleIndex = 0)
{
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

    /// <summary>清單只顯示第一行；延續行在詳細資訊中顯示。</summary>
    public string Message
    {
        get
        {
            var text = DisplayText(Event.Message);
            var i = text.IndexOf('\n');
            return i < 0 ? text : text[..i] + " …";
        }
    }

    /// <summary>分軌檢視：每個模組一欄，只在事件所屬模組的欄位顯示訊息。</summary>
    public string this[string module] => string.Equals(module, Module, StringComparison.Ordinal) ? Message : "";

    /// <summary>原始位置，例如「20300107/PLC-2030-01-07/PLCLOG.txt 第 3～5 行」。</summary>
    public string Source => FormatSource(Event.Source);

    public static string FormatSource(EventSource s) =>
        s.LineStart == s.LineEnd
            ? $"{s.RelativePath} 第 {s.LineStart} 行"
            : $"{s.RelativePath} 第 {s.LineStart}～{s.LineEnd} 行";

    /// <summary>
    /// 畫面顯示用：編碼錯誤以替代字元顯示（ADR-0004 第 6 點）。Big5 解碼器產生的私用區字元也換成替代字元；
    /// 儲存的內容與原始檔不受影響。
    /// </summary>
    public static string DisplayText(string text)
    {
        if (!LogEncoding.HasDecodingError(text)) return text;
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text) sb.Append(ch is >= '\uE000' and <= '\uF8FF' ? '\uFFFD' : ch);
        return sb.ToString();
    }
}
