using System.Text;

namespace TechValidation.Core;

/// <summary>
/// 搜尋用文字的正規化（ADR-0010、ADR-0011）：NFKC 加大小寫摺疊。
/// 匯入時對事件內容、搜尋時對查詢字串做同樣的處理。
/// </summary>
public static class TextNormalizer
{
    public static string Normalize(string text)
    {
        if (text.Length == 0) return text;
        if (IsAscii(text)) return text.ToLowerInvariant();
        return text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
    }

    static bool IsAscii(string s) => Ascii.IsValid(s);

    /// <summary>確認執行環境的正規化有效（InvariantGlobalization 下會失效或拋出例外）。</summary>
    public static bool SelfCheck(out string detail)
    {
        try
        {
            var ok = Normalize("ＡＢＣ１２３") == "abc123" && Normalize("ｱﾗｰﾑ") == "アラーム";
            detail = ok ? "NFKC OK" : "NFKC 結果不符";
            return ok;
        }
        catch (Exception ex)
        {
            detail = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }
}
