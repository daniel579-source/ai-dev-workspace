namespace TechValidation.Core;

/// <summary>T-13 的測試表（虛構案例）。單元測試與量測報告共用。</summary>
public static class NormalizationCases
{
    public sealed record Case(string Name, string Input, string Expected);

    public static readonly Case[] All =
    [
        new("全形英數", "ＥＲＲ０４２", "err042"),
        new("全形連字號", "ＥＲＲ－０４２", "err-042"),
        new("大寫英文", "Station_Status_Change", "station_status_change"),
        new("半形片假名", "ｱﾗｰﾑ", "アラーム"),
        new("半形片假名（濁音）", "ｶﾞｲﾄﾞ", "ガイド"),
        new("全形符號", "！＃＄％（）＝", "!#$%()="),
        new("全形空白", "A　B", "a b"),
        new("圈數字", "①②", "12"),
        new("全形冒號與數字", "１２：３４", "12:34"),
        new("中文不變", "在席", "在席"),
        new("相容漢字（㈱）", "㈱", "(株)"),
        new("德文 ß（簡單大小寫摺疊不轉 ss）", "STRAßE", "straße"),
        new("希臘字母", "ΣΑΣ", "σασ"),
        new("組合字元（é 兩種寫法）", "é", "é"),
    ];
}
