using LogTrace.Engine.Events;

namespace LogTrace.Engine.Storage;

/// <summary>匯入案件的一個原始檔（複本位於案件的 raw\ 下，路徑相對於 raw\）。</summary>
public sealed record StoredFile(string Module, string DayFolder, string Logger, string RelativePath, long Size, string Sha256);

/// <summary>案件的中繼資料。</summary>
public sealed record CaseInfo(string Name, string PlantName, IReadOnlyList<string> ModuleOrder, DateTimeOffset CreatedAt);

/// <summary>
/// 儲存層介面（ADR-0001：儲存層要可以替換；ADR-0011：目前實作為 SQLite）。
/// 事件依事件序號儲存與讀取。
/// </summary>
public interface IEventStore : IDisposable
{
    void SaveInfo(CaseInfo info);
    CaseInfo LoadInfo();

    /// <summary>以單一交易寫入一次匯入的檔案與事件。</summary>
    void SaveImport(IReadOnlyList<StoredFile> files, IReadOnlyList<LogEvent> events);

    long GetEventCount();
    IReadOnlyList<LogEvent> GetEvents(long fromSequence, int maxCount);
    IReadOnlyList<StoredFile> GetFiles();
}
