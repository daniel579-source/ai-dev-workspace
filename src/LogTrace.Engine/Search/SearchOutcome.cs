namespace LogTrace.Engine.Search;

/// <summary>搜尋要不要使用全文索引。</summary>
public enum SearchPath
{
    /// <summary>依 ADR-0011 第 6 點的規則選擇：有精確篩選時先用參照表；罕見的 3 字以上關鍵字用索引；其餘掃描。</summary>
    Auto,

    /// <summary>一律掃描（比對結果、索引尚未建立）。</summary>
    ScanOnly,
}

/// <summary>搜尋結果與執行方式。</summary>
/// <param name="Sequences">符合的事件序號，依時間排序。</param>
/// <param name="IndexedDays">以全文索引搜尋的天數。</param>
/// <param name="ScannedDays">以掃描搜尋的天數。</param>
/// <param name="ByFields">以解析欄位的參照表縮小範圍。</param>
public sealed record SearchOutcome(IReadOnlyList<long> Sequences, int IndexedDays, int ScannedDays, bool ByFields);

/// <summary>案件中的一個日期資料夾：事件序號範圍與原始 LOG 大小（全文索引依日分片）。</summary>
public sealed record SearchDay(string Day, long FirstSequence, long LastSequence, long RawBytes);
