using LogTrace.Engine.Events;
using LogTrace.Engine.Import;
using LogTrace.Engine.Plants;
using LogTrace.Engine.Storage;

namespace LogTrace.Engine.Cases;

/// <summary>
/// 案件：一次分析的工作單位（GLOSSARY）。案件資料夾的結構（ADR-0011 第 2 點）：
/// case.db（不可重建）、raw\（原始檔複本，不可重建）；index\（可重建）在後續 ticket 加入。
/// </summary>
public sealed class Case : IDisposable
{
    public const string DatabaseFileName = "case.db";
    public const string RawFolderName = "raw";

    readonly IEventStore _store;

    public string Folder { get; }
    public CaseInfo Info { get; }
    public string RawFolder => Path.Combine(Folder, RawFolderName);

    Case(string folder, IEventStore store, CaseInfo info)
    {
        Folder = folder;
        _store = store;
        Info = info;
    }

    public static Case Create(string folder, string name, PlantConfig plant)
    {
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            throw new IOException($"案件資料夾已存在：{folder}");
        Directory.CreateDirectory(Path.Combine(folder, RawFolderName));
        var store = new SqliteEventStore(Path.Combine(folder, DatabaseFileName));
        var info = new CaseInfo(name, plant.Name, plant.ModuleOrder, DateTimeOffset.Now);
        store.SaveInfo(info);
        return new Case(folder, store, info);
    }

    public static Case Open(string folder)
    {
        var db = Path.Combine(folder, DatabaseFileName);
        if (!File.Exists(db)) throw new FileNotFoundException("不是案件資料夾", db);
        var store = new SqliteEventStore(db);
        return new Case(folder, store, store.LoadInfo());
    }

    PlantConfig Plant => new() { Name = Info.PlantName, ModuleOrder = Info.ModuleOrder };

    /// <summary>匯入一個「日期 → 模組」結構的資料夾。</summary>
    public ImportResult Import(string sourceFolder, CancellationToken ct = default) =>
        CaseImporter.Import(sourceFolder, RawFolder, Plant, _store, ct);

    public long EventCount => _store.GetEventCount();

    /// <summary>從指定事件序號起，依序號遞增讀取事件。</summary>
    public IReadOnlyList<LogEvent> GetEvents(long fromSequence, int maxCount) => _store.GetEvents(fromSequence, maxCount);

    public IReadOnlyList<StoredFile> GetFiles() => _store.GetFiles();

    public void Dispose() => _store.Dispose();
}
