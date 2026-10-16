using System.Text;
using System.Text.RegularExpressions;
using System.Text.Unicode;

namespace TechValidation.Core;

/// <summary>
/// 共通容器格式 `時間 | 等級 | Logger | 訊息` 的解析（docs/log-format-notes.md 第 2 節、ADR-0004）。
/// 技術驗證用的簡化版：解析欄位的規則寫死在這裡，正式版改放廠別設定（#23）。
/// </summary>
public static partial class LogParser
{
    static readonly Encoding Utf8Strict = new UTF8Encoding(false, true);
    static readonly Encoding Big5;

    static LogParser()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Big5 = Encoding.GetEncoding(950, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
    }

    public sealed class FileResult
    {
        public required List<ParsedEvent> Events { get; init; }
        public required string Encoding { get; init; }
        public long FirstValidTicks { get; init; }
        public long LastValidTicks { get; init; }
        public bool Unparsable { get; init; }
    }

    public static FileResult ParseFile(LogFile file, byte[] bytes)
    {
        var isUtf8 = Utf8.IsValid(bytes);
        var enc = isUtf8 ? Utf8Strict : Big5;
        var events = new List<ParsedEvent>(Math.Max(16, bytes.Length / 600));

        int lineNo = 0;
        int pos = 0;
        ParsedEvent? cur = null;
        long curMsgStart = 0;
        int lastNonBlankLine = 0;
        long lastNonBlankEnd = 0;
        long prevValid = long.MinValue;
        long firstValid = long.MinValue;

        void Finish()
        {
            if (cur is null) return;
            cur.LineEnd = lastNonBlankLine;
            cur.ByteLength = (int)(lastNonBlankEnd - cur.ByteOffset);
            var msg = Decode(enc, bytes.AsSpan((int)curMsgStart, (int)(lastNonBlankEnd - curMsgStart)));
            if (!isUtf8 && HasSuspiciousChars(msg)) cur.Flags |= EventFlags.EncodingError;
            ExtractFields(cur, msg);
            events.Add(cur);
            cur = null;
        }

        while (pos < bytes.Length)
        {
            lineNo++;
            int nl = bytes.AsSpan(pos).IndexOf((byte)'\n');
            int lineEnd = nl < 0 ? bytes.Length : pos + nl;      // 不含 \n
            int contentEnd = lineEnd;
            while (contentEnd > pos && (bytes[contentEnd - 1] == (byte)'\r' || bytes[contentEnd - 1] == (byte)' ' || bytes[contentEnd - 1] == (byte)'\t'))
                contentEnd--;
            var line = bytes.AsSpan(pos, contentEnd - pos);

            if (TryParseHeader(line, out var ticks, out var valid, out var level, out var msgOffset))
            {
                Finish();
                var ev = new ParsedEvent { FileId = file.FileId, LineStart = lineNo, ByteOffset = pos, Level = level };
                if (valid)
                {
                    if (prevValid != long.MinValue && ticks < prevValid) ev.Flags |= EventFlags.TimeBackward;
                    prevValid = ticks;
                    if (firstValid == long.MinValue) firstValid = ticks;
                }
                else
                {
                    // ADR-0004 第 5 點：沿用同一檔案中前一筆有效事件的時間
                    ev.Flags |= EventFlags.TimeInvalid;
                    ticks = prevValid == long.MinValue ? 0 : prevValid;
                }
                if (level == 255) ev.Flags |= EventFlags.UnknownKind;
                ev.Ticks = ticks;
                cur = ev;
                curMsgStart = Math.Min(pos + msgOffset, contentEnd);
                lastNonBlankLine = lineNo;
                lastNonBlankEnd = contentEnd;
            }
            else if (cur is not null && line.Length > 0)
            {
                lastNonBlankLine = lineNo;
                lastNonBlankEnd = contentEnd;
            }
            pos = nl < 0 ? bytes.Length : lineEnd + 1;
        }
        Finish();

        return new FileResult
        {
            Events = events,
            Encoding = isUtf8 ? "UTF-8" : "Big5",
            FirstValidTicks = firstValid,
            LastValidTicks = prevValid,
            Unparsable = events.Count == 0 && bytes.Length > 0,
        };
    }

    /// <summary>
    /// Windows 的 CP950 不會把無效位元組變成替代字元：例如 0xFF 對應到私用區 U+F8F8。
    /// 因此替代字元與私用區字元都視為可疑（私用區也可能是造字，待確認）。
    /// </summary>
    static bool HasSuspiciousChars(string s)
    {
        foreach (var ch in s)
            if (ch == '�' || (ch >= '' && ch <= '')) return true;
        return false;
    }

    static string Decode(Encoding enc, ReadOnlySpan<byte> span)
    {
        // 延續行以 \n 連接；去除行尾 \r 與空白
        var s = enc.GetString(span);
        if (s.IndexOf('\r') >= 0) s = s.Replace("\r", "");
        return s;
    }

    /// <summary>判斷是否為紀錄開頭：`yyyy-MM-dd HH:mm:ss.ffff | LEVEL | Logger | `。</summary>
    public static bool TryParseHeader(ReadOnlySpan<byte> line, out long ticks, out bool valid, out byte level, out int msgOffset)
    {
        ticks = 0; valid = false; level = 255; msgOffset = 0;
        if (line.Length < 27) return false;
        // 位置：0-3 年 4 '-' 5-6 月 7 '-' 8-9 日 10 ' ' 11-12 時 13 ':' 14-15 分 16 ':' 17-18 秒 19 '.' 20-23 小數
        if (line[4] != '-' || line[7] != '-' || line[10] != ' ' || line[13] != ':' || line[16] != ':' || line[19] != '.') return false;
        for (int i = 0; i < 24; i++)
        {
            if (i is 4 or 7 or 10 or 13 or 16 or 19) continue;
            if ((uint)(line[i] - '0') > 9) return false;
        }
        if (!line[24..].StartsWith(" | "u8)) return false;
        var rest = line[27..];
        int p1 = rest.IndexOf(" | "u8);
        if (p1 < 0) return false;
        var levelSpan = rest[..p1];
        var afterLevel = rest[(p1 + 3)..];
        int p2 = afterLevel.IndexOf(" | "u8);
        int msgStartInRest;
        if (p2 < 0)
        {
            // 只有 Logger 沒有訊息（例如 "| Logger |" 結尾已被修剪）
            if (afterLevel.EndsWith(" |"u8)) msgStartInRest = rest.Length;
            else return false;
        }
        else msgStartInRest = p1 + 3 + p2 + 3;
        level = Levels.Parse(levelSpan);
        msgOffset = 27 + msgStartInRest;

        int y = D(line, 0, 4), mo = D(line, 5, 2), d = D(line, 8, 2), h = D(line, 11, 2), mi = D(line, 14, 2), s = D(line, 17, 2), f = D(line, 20, 4);
        if (mo is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(Math.Clamp(y, 1, 9999), mo) || h > 23 || mi > 59 || s > 59 || y < 1)
            return true; // 外觀是紀錄開頭，但時間無效
        ticks = new DateTime(y, mo, d, h, mi, s).Ticks + f * 1000L;
        valid = true;
        return true;

        static int D(ReadOnlySpan<byte> l, int start, int len)
        {
            int v = 0;
            for (int i = 0; i < len; i++) v = v * 10 + (l[start + i] - '0');
            return v;
        }
    }

    // ---- 解析欄位（驗證用的固定規則；正式版放在廠別設定） ----

    [GeneratedRegex(@"(?<![0-9])000[0-9]{5}(?![0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex CommandRx();

    [GeneratedRegex(@"棧板命令\s*=\s*([0-9]{9})(?![0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex PalletRx();

    [GeneratedRegex(@"^\[([a-z]-[0-9]{2})\]\[station_status_change\] => (.+?) = (on|off)$", RegexOptions.CultureInvariant)]
    private static partial Regex StationRx();

    static void ExtractFields(ParsedEvent ev, string msg)
    {
        var norm = TextNormalizer.Normalize(msg);
        ev.NormText = norm;

        if (norm.Contains("000", StringComparison.Ordinal))
        {
            List<long>? cmds = null;
            foreach (var m in CommandRx().EnumerateMatches(norm))
            {
                var v = long.Parse(norm.AsSpan(m.Index, m.Length));
                cmds ??= new();
                if (!cmds.Contains(v)) cmds.Add(v);
            }
            ev.Commands = cmds?.ToArray();
        }
        if (norm.Contains("棧板命令", StringComparison.Ordinal))
        {
            var pallets = PalletRx().Matches(norm).Select(m => long.Parse(m.Groups[1].ValueSpan)).Distinct().ToArray();
            if (pallets.Length > 0) ev.Pallets = pallets;
        }
        if (norm.StartsWith('[') && norm.Contains("station_status_change", StringComparison.Ordinal))
        {
            var m = StationRx().Match(norm);
            if (m.Success) ev.Station = new StationChange(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].ValueSpan.SequenceEqual("on"));
        }
    }
}
