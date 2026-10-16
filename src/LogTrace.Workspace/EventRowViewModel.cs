using System.Globalization;
using LogTrace.Engine.Events;

namespace LogTrace.Workspace;

/// <summary>事件清單的一列。</summary>
public sealed class EventRowViewModel(LogEvent e)
{
    public LogEvent Event { get; } = e;

    public long Sequence => Event.Sequence;
    public string Time => Event.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.ffff", CultureInfo.InvariantCulture);
    public string Module => Event.Module;
    public string Logger => Event.Logger;
    public string Level => Event.Level;

    /// <summary>清單只顯示第一行；延續行在詳細資訊中顯示。</summary>
    public string Message
    {
        get
        {
            var i = Event.Message.IndexOf('\n');
            return i < 0 ? Event.Message : Event.Message[..i] + " …";
        }
    }

    /// <summary>原始位置，例如「20300107/PLC-2030-01-07/PLCLOG.txt 第 3～5 行」。</summary>
    public string Source => FormatSource(Event.Source);

    public static string FormatSource(EventSource s) =>
        s.LineStart == s.LineEnd
            ? $"{s.RelativePath} 第 {s.LineStart} 行"
            : $"{s.RelativePath} 第 {s.LineStart}～{s.LineEnd} 行";
}
