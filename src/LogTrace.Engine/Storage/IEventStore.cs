using LogTrace.Engine.Events;

namespace LogTrace.Engine.Storage;

/// <summary>
/// 案件內的一個原始檔版本（ADR-0002 第 4、5 點）。同一個相對路徑可以有多個版本（重複匯入時內容不同），
/// 舊版本永遠保留；同一時間只有一個版本參與查詢（Active）。
/// </summary>
/// <param name="RelativePath">從日期資料夾起算的路徑，例如 20300107/PLC-2030-01-07/PLCLOG.txt；也是穩定識別碼的一部分。</param>
/// <param name="Version">版本，從 1 起算。</param>
/// <param name="StoragePath">複本在案件 raw\ 下的路徑：第 1 版與相對路徑相同，之後的版本放在 _versions\&lt;版本&gt;\ 下。</param>
/// <param name="StreamKey">同一 Logger 的輪替順序（見 SourceFile.StreamKey）。</param>
/// <param name="Encoding">解碼使用的編碼（UTF-8 或 Big5）。</param>
/// <param name="Unparsable">整個檔案不符合共通格式，沒有產生事件（ADR-0004 第 6 點），可以用原始文字的方式開啟。</param>
/// <param name="SourcePath">匯入時的原始位置（資料夾中的檔案，或「壓縮檔路徑|壓縮檔內的路徑」）。</param>
public sealed record StoredFile(
    string RelativePath, int Version, string StoragePath, bool Active,
    string Module, string DayFolder, string Logger, string StreamKey,
    long Size, string Sha256, string Encoding, int EventCount, bool Unparsable,
    long ImportId, string SourcePath, DateTimeOffset ImportedAt);

/// <summary>一次匯入（ADR-0002 第 4 點：記錄原始路徑與匯入時間；壓縮檔另存一份在案件的 sources\ 下）。</summary>
/// <param name="ArchiveCopy">壓縮檔在案件 sources\ 下的檔名；來源是資料夾時為 null。</param>
public sealed record ImportRecord(long Id, DateTimeOffset Time, string SourcePath, string SourceKind, string? ArchiveSha256, string? ArchiveCopy);

/// <summary>稽核紀錄的一筆（ADR-0009 第 5 點）：操作人（作業系統帳號）、時間、操作種類、對象與內容。</summary>
public sealed record AuditEntry(DateTimeOffset Time, string User, string Action, string Target, string Detail)
{
    public static string CurrentUser => $@"{Environment.UserDomainName}\{Environment.UserName}";

    public static AuditEntry Now(string action, string target, string detail = "") =>
        new(DateTimeOffset.Now, CurrentUser, action, target, detail);
}

/// <summary>案件的中繼資料。Plant 是建立案件時所選廠別設定的匯入相關部分（ADR-0002 第 7 點）。</summary>
public sealed record CaseInfo(string Name, LogTrace.Engine.Plants.PlantConfig Plant, DateTimeOffset CreatedAt);

/// <summary>
/// 儲存層介面（ADR-0001：儲存層要可以替換；ADR-0011：目前實作為 SQLite）。
/// 事件依事件序號儲存與讀取。
/// </summary>
public interface IEventStore : IDisposable
{
    void SaveInfo(CaseInfo info);
    CaseInfo LoadInfo();

    long NextImportId();

    /// <summary>
    /// 以單一交易寫入一次匯入：匯入紀錄、案件中所有的檔案版本（取代原本的清單）、重新排序後的全部事件、稽核紀錄。
    /// 事件以 RelativePath 對應到該路徑目前參與查詢的版本。
    /// </summary>
    void SaveImport(ImportRecord import, IReadOnlyList<StoredFile> files, IReadOnlyList<LogEvent> events,
        IReadOnlyList<Parsing.EventField> fields, IReadOnlyList<Parsing.EventEffect> effects, IReadOnlyList<AuditEntry> audit);

    /// <summary>以新的解析規則重新抽出的欄位與效果取代原本的，並更新案件保存的規則（單一交易）。</summary>
    void ReplaceFields(IReadOnlyList<Parsing.EventField> fields, IReadOnlyList<Parsing.EventEffect> effects, CaseInfo info, AuditEntry audit);

    /// <summary>所有效果，依事件序號（世界模型的輸入，ADR-0005）。</summary>
    IReadOnlyList<Parsing.EventEffect> GetEffects();

    /// <summary>一筆事件的解析欄位。</summary>
    IReadOnlyList<Parsing.EventField> GetFields(long sequence);

    /// <summary>指定種類的欄位值與出現的事件數（值不分大小寫）。</summary>
    IReadOnlyList<(string Kind, string Value, long Count)> GetFieldValueCounts(IReadOnlyList<string> kinds);

    /// <summary>有任一指定種類的欄位、值為任一指定值（不分大小寫）的事件序號，依序號排列。</summary>
    IReadOnlyList<long> FindSequences(IReadOnlyList<string> kinds, IReadOnlyList<string> values);

    void AddAudit(AuditEntry entry);
    IReadOnlyList<AuditEntry> GetAudit();

    long GetEventCount();
    IReadOnlyList<LogEvent> GetEvents(long fromSequence, int maxCount);

    /// <summary>所有檔案版本（含不參與查詢的舊版本）。</summary>
    IReadOnlyList<StoredFile> GetFiles();
    IReadOnlyList<ImportRecord> GetImports();
}
