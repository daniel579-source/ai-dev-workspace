using LogTrace.Engine.Events;

namespace LogTrace.Workspace;

/// <summary>旗標的圖示、名稱與說明（ADR-0008 第 5 點：以圖示呈現並附圖例，滑鼠移上去顯示說明）。</summary>
public sealed record FlagInfo(EventFlags Flag, string Glyph, string Name, string Description)
{
    /// <summary>圖例的順序，也是事件清單中圖示的順序。</summary>
    public static IReadOnlyList<FlagInfo> All { get; } =
    [
        new(EventFlags.SameTimestamp, "＝", "同時間戳記",
            "有其他事件的時間完全相同。清單中的先後只為了排序穩定（同一檔案依行號，跨檔案依模組順序），不代表因果。"),
        new(EventFlags.TimeBackward, "↶", "時間倒退",
            "這筆事件的時間早於同一 LOG 中前一筆事件。清單依時間排序，原始位置可以在原始 LOG 面板看到。"),
        new(EventFlags.TimeInvalid, "⌀", "時間無效（時間為沿用）",
            "時間戳記無法解讀，或這段內容沒有時間戳記；時間沿用同一 LOG 中前一筆有效事件（沒有前一筆時沿用後一筆）。"),
        new(EventFlags.UnknownKind, "？", "未知種類",
            "不在已知 LOG 種類中的 Logger，或檔案開頭沒有時間戳記的內容。內容照樣保存，沒有丟棄。"),
        new(EventFlags.EncodingError, "\uFFFD", "編碼錯誤",
            "有無法解碼的位元組，以替代字元顯示；原始位元組保留在原始檔中。"),
    ];

    public static IReadOnlyList<FlagInfo> Of(EventFlags flags) =>
        flags == EventFlags.None ? [] : All.Where(f => flags.HasFlag(f.Flag)).ToList();
}
