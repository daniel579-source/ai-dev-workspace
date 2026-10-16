using System.Globalization;
using System.Text;
using LogTrace.Engine.Events;

namespace LogTrace.Engine.Parsing;

/// <summary>解析後、尚未排序與編序號的事件。沒有時間戳記的事件（檔案開頭的前置內容），Level 與 Logger 為空字串。</summary>
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

/// <summary>一個檔案的解析結果。</summary>
/// <param name="Unparsable">整個檔案沒有任何以時間戳記開頭的行（ADR-0004 第 6 點）：不產生事件。</param>
public sealed record ParseResult(IReadOnlyList<ParsedEvent> Events, string Encoding, bool Unparsable);

/// <summary>
/// 共通容器格式 `時間 | 等級 | Logger | 訊息` 的切分（ADR-0004 第 1、6 點）：
/// <list type="bullet">
/// <item>以行首時間戳記開始一筆事件，其後不以時間戳記開頭的行為延續行；紀錄之間的空行與行尾空白不算進事件。</item>
/// <item>外觀是時間戳記、但日期或時間無效的行，仍然開始一筆事件，標示「時間無效」。</item>
/// <item>第一個時間戳記之前的內容（前置內容）不丟棄，成為一筆標示「時間無效」與「未知種類」的事件。</item>
/// <item>無法解碼的位元組保留在原始檔，事件標示「編碼錯誤」。</item>
/// </list>
/// </summary>
public static class LogRecordParser
{
    const int TimestampLength = 24; // yyyy-MM-dd HH:mm:ss.ffff

    public static ParseResult Parse(ReadOnlySpan<byte> bytes) => Parse(bytes, LogEncoding.Detect(bytes));

    public static ParseResult Parse(ReadOnlySpan<byte> bytes, string encodingName)
    {
        var encoding = LogEncoding.Get(encodingName);
        var events = new List<ParsedEvent>();
        Pending? cur = null;
        bool sawHeader = false;
        int lineNo = 0, pos = 0;

        while (pos < bytes.Length)
        {
            lineNo++;
            int nl = bytes[pos..].IndexOf((byte)'\n');
            int lineEnd = nl < 0 ? bytes.Length : pos + nl;
            int contentEnd = lineEnd;
            while (contentEnd > pos && bytes[contentEnd - 1] is (byte)'\r' or (byte)' ' or (byte)'\t') contentEnd--;
            var line = bytes[pos..contentEnd];

            if (TryParseHeader(line, encoding, out var header))
            {
                if (cur is not null) events.Add(cur.Finish(bytes, encoding));
                cur = new Pending(header, lineNo, pos, pos + header.MessageOffset, contentEnd);
                sawHeader = true;
            }
            else if (line.Length > 0)
            {
                if (cur is null) cur = new Pending(null, lineNo, pos, pos, contentEnd);
                else
                {
                    cur.LastLine = lineNo;
                    cur.LastEnd = contentEnd;
                }
            }
            pos = nl < 0 ? bytes.Length : lineEnd + 1;
        }
        if (cur is not null) events.Add(cur.Finish(bytes, encoding));

        return sawHeader ? new ParseResult(events, encodingName, false) : new ParseResult([], encodingName, true);
    }

    /// <summary>正在累積延續行的事件；header 為 null 表示檔案開頭的前置內容。</summary>
    sealed class Pending(Header? header, int lineStart, long byteOffset, long messageStart, long firstLineEnd)
    {
        readonly int _lineStart = lineStart;
        public int LastLine = lineStart;
        public long LastEnd = firstLineEnd;

        public ParsedEvent Finish(ReadOnlySpan<byte> bytes, Encoding encoding)
        {
            long msgStart = Math.Min(messageStart, LastEnd);
            // 行尾空白不算進事件內容（ADR-0004 第 1 點）；延續行以 \n 連接
            var lines = encoding.GetString(bytes[(int)msgStart..(int)LastEnd]).Split('\n');
            var message = string.Join('\n', lines.Select(l => l.TrimEnd(' ', '\t', '\r')));

            var flags = EventFlags.None;
            if (header is not { Valid: true }) flags |= EventFlags.TimeInvalid;
            if (header is null) flags |= EventFlags.UnknownKind;
            if (LogEncoding.HasDecodingError(message) || header is { HasDecodingError: true }) flags |= EventFlags.EncodingError;

            var h = header ?? default;
            return new ParsedEvent(h.Timestamp, h.RawTimestamp ?? "", h.Level ?? "", h.Logger ?? "", message,
                _lineStart, LastLine, byteOffset, (int)(LastEnd - byteOffset), flags);
        }
    }

    readonly record struct Header(DateTime Timestamp, bool Valid, string RawTimestamp, string Level, string Logger, int MessageOffset, bool HasDecodingError);

    static bool TryParseHeader(ReadOnlySpan<byte> line, Encoding encoding, out Header header)
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

        var raw = System.Text.Encoding.ASCII.GetString(line[..TimestampLength]);
        bool valid = DateTime.TryParseExact(raw, "yyyy-MM-dd HH:mm:ss.ffff",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts);
        string levelText = encoding.GetString(level), loggerText = encoding.GetString(logger);
        header = new Header(ts, valid, raw, levelText, loggerText, messageOffset,
            LogEncoding.HasDecodingError(levelText) || LogEncoding.HasDecodingError(loggerText));
        return true;
    }
}
