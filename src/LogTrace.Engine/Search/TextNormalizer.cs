using System.Globalization;
using System.Text;

namespace LogTrace.Engine.Search;

/// <summary>
/// 搜尋用文字的正規化（ADR-0010 第 1 點、ADR-0011 第 2 點）：NFKC（全形與半形相同）加大小寫摺疊。
/// 匯入時對事件內容、搜尋時對查詢字串做同樣的處理。
/// 以「字」（grapheme）為單位逐一正規化，並記錄每個正規化後的字元來自原文的哪個位置，
/// 讓醒目標示的位置與原文對應正確。程式不得啟用 InvariantGlobalization（ADR-0011）。
/// </summary>
public static class TextNormalizer
{
    /// <summary>正規化（不分大小寫）。</summary>
    public static string Normalize(string text) => Normalize(text, caseSensitive: false);

    public static string Normalize(string text, bool caseSensitive)
    {
        // 大部分訊息已經是 NFKC（ASCII、一般中文）：只需要大小寫摺疊，結果與逐字正規化相同
        if (Ascii.IsValid(text) || text.IsNormalized(NormalizationForm.FormKC)) return caseSensitive ? text : text.ToLowerInvariant();
        return NormalizeWithMap(text, caseSensitive).Text;
    }

    /// <summary>正規化，並傳回每個正規化後字元在原文中的起點與長度。</summary>
    public static (string Text, int[] Start, int[] Length) NormalizeWithMap(string text, bool caseSensitive = false)
    {
        var sb = new StringBuilder(text.Length);
        var start = new List<int>(text.Length);
        var length = new List<int>(text.Length);
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
        {
            var element = (string)e.Current;
            var n = Ascii.IsValid(element) ? element : element.Normalize(NormalizationForm.FormKC);
            if (!caseSensitive) n = n.ToLowerInvariant();
            foreach (var ch in n)
            {
                sb.Append(ch);
                start.Add(e.ElementIndex);
                length.Add(element.Length);
            }
        }
        return (sb.ToString(), start.ToArray(), length.ToArray());
    }
}
