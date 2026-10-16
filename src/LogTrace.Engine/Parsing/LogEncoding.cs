using System.Text;

namespace LogTrace.Engine.Parsing;

/// <summary>
/// LOG 檔的文字編碼（docs/log-format-notes.md O-002：UTF-8 與 Big5 混用、沒有 BOM）。
/// 依檔案判斷：整個檔案是有效的 UTF-8 就當 UTF-8，否則當 Big5（CP950）。
/// 依檔案指定編碼的廠別設定在後續 ticket 加入；自動判斷只是輔助。
/// </summary>
public static class LogEncoding
{
    public const string Utf8Name = "UTF-8";
    public const string Big5Name = "Big5";

    static readonly Encoding Utf8 = new UTF8Encoding(false, false);
    static readonly Encoding Big5;

    static LogEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        // DecoderFallback.ReplacementFallback 的替代字串是「?」，無法與正常的問號區分；明確指定 U+FFFD
        Big5 = Encoding.GetEncoding(950, EncoderFallback.ReplacementFallback, new DecoderReplacementFallback("\uFFFD"));
    }

    public static string Detect(ReadOnlySpan<byte> bytes) => System.Text.Unicode.Utf8.IsValid(bytes) ? Utf8Name : Big5Name;

    public static Encoding Get(string name) => name switch
    {
        Utf8Name => Utf8,
        Big5Name => Big5,
        _ => throw new ArgumentException($"不支援的編碼：{name}", nameof(name)),
    };

    /// <summary>
    /// ADR-0004 第 6 點：解碼結果含替代字元或私用區字元（U+E000～U+F8FF）時視為編碼錯誤。
    /// Windows 的 CP950 會把部分無效位元組（例如 0xFF）變成私用區字元，而不是替代字元。
    /// 私用區字元也可能是廠內造字（待確認）。
    /// </summary>
    public static bool HasDecodingError(string text)
    {
        foreach (var ch in text)
            if (ch == '\uFFFD' || ch is >= '\uE000' and <= '\uF8FF') return true;
        return false;
    }
}
