using System.Text;
using LogTrace.Engine.Events;

namespace LogTrace.Engine.Parsing;

/// <summary>解析後、尚未排序與編序號的事件。</summary>
public sealed record ParsedEvent(
    DateTime Timestamp,
    string RawTimestamp,
    string Level,
    string Logger,
    string Message,
    int LineStart,
    int LineEnd,
    long ByteOffset,
    int ByteLength,
    EventFlags Flags);

/// <summary>
/// 共通容器格式 `時間 | 等級 | Logger | 訊息` 的切分（ADR-0004 第 1 點）：
/// 以行首時間戳記開始一筆事件，其後不以時間戳記開頭的行為延續行；紀錄之間的空行與行尾空白不算進事件。
/// 目前以 UTF-8 解碼；Big5、時間倒退等旗標在後續 ticket 補齊。
/// </summary>
public static class LogRecordParser
{
    const int TimestampLength = 24; // yyyy-MM-dd HH:mm:ss.ffff
    static readonly Encoding Utf8 = new UTF8Encoding(false, false);

    public static IReadOnlyList<ParsedEvent> Parse(ReadOnlySpan<byte> bytes)
    {
        var events = new List<ParsedEvent>();
        Pending? cur = null;
        int lineNo = 0, pos = 0;

        while (pos < bytes.Length)
        {
            lineNo++;
            int nl = bytes[pos..].IndexOf((byte)'\n');
            int lineEnd = nl < 0 ? bytes.Length : pos + nl;
            int contentEnd = lineEnd;
            while (contentEnd > pos && bytes[contentEnd - 1] is (byte)'\r' or (byte)' ' or (byte)'\t') contentEnd--;
            var line = bytes[pos..contentEnd];

            if (TryParseHeader(line, out var header))
            {
                if (cur is not null) events.Add(cur.Finish(bytes));
                cur = new Pending(header, lineNo, pos, pos + header.MessageOffset, contentEnd);
            }
            else if (cur is not null && line.Length > 0)
            {
                cur.LastLine = lineNo;
                cur.LastEnd = contentEnd;
            }
            pos = nl < 0 ? bytes.Length : lineEnd + 1;
        }
        if (cur is not null) events.Add(cur.Finish(bytes));
        return events;
    }

    /// <summary>正在累積延續行的事件。</summary>
    sealed class Pending(Header header, int lineStart, long byteOffset, long messageStart, long firstLineEnd)
    {
        readonly Header _header = header;
        readonly int _lineStart = lineStart;
        readonly long _byteOffset = byteOffset;
        readonly long _messageStart = messageStart;
        public int LastLine = lineStart;
        public long LastEnd = firstLineEnd;

        public ParsedEvent Finish(ReadOnlySpan<byte> bytes)
        {
            long msgStart = Math.Min(_messageStart, LastEnd);
            // 行尾空白不算進事件內容（ADR-0004 第 1 點）；延續行以 \n 連接
            var lines = Utf8.GetString(bytes[(int)msgStart..(int)LastEnd]).Split('\n');
            var message = string.Join('\n', lines.Select(l => l.TrimEnd(' ', '\t', '\r')));
            return new ParsedEvent(_header.Timestamp, _header.RawTimestamp, _header.Level, _header.Logger, message,
                _lineStart, LastLine, _byteOffset, (int)(LastEnd - _byteOffset), _header.Valid ? EventFlags.None : EventFlags.TimeInvalid);
        }
    }

    readonly record struct Header(DateTime Timestamp, bool Valid, string RawTimestamp, string Level, string Logger, int MessageOffset);

    static bool TryParseHeader(ReadOnlySpan<byte> line, out Header header)
    {
        header = default;
        if (line.Length < TimestampLength + 3) return false;
        for (int i = 0; i < TimestampLength; i++)
        {
            byte b = line[i];
            bool ok = i switch
            {
                4 or 7 => b == '-',
                10 => b == ' ',
                13 or 16 => b == ':',
                19 => b == '.',
                _ => (uint)(b - '0') <= 9,
            };
            if (!ok) return false;
        }
        var rest = line[TimestampLength..];
        if (!rest.StartsWith(" | "u8)) return false;
        rest = rest[3..];
        int p1 = rest.IndexOf(" | "u8);
        if (p1 < 0) return false;
        var level = rest[..p1];
        var afterLevel = rest[(p1 + 3)..];
        int p2 = afterLevel.IndexOf(" | "u8);
        ReadOnlySpan<byte> logger;
        int messageOffset;
        if (p2 >= 0)
        {
            logger = afterLevel[..p2];
            messageOffset = TimestampLength + 3 + p1 + 3 + p2 + 3;
        }
        else if (afterLevel.EndsWith(" |"u8))
        {
            logger = afterLevel[..^2];
            messageOffset = line.Length;
        }
        else return false;

        var raw = Encoding.ASCII.GetString(line[..TimestampLength]);
        bool valid = DateTime.TryParseExact(raw, "yyyy-MM-dd HH:mm:ss.ffff",
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var ts);
        header = new Header(ts, valid, raw, Utf8.GetString(level), Utf8.GetString(logger), messageOffset);
        return true;
    }
}
