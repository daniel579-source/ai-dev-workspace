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
    readonly RawLogReader _raw;
    Dictionary<string, StoredFile>? _files;

    public string Folder { get; }
    public CaseInfo Info { get; }
    public string RawFolder => Path.Combine(Folder, RawFolderName);

    Case(string folder, IEventStore store, CaseInfo info)
    {
        Folder = folder;
        _store = store;
        Info = info;
        _raw = new RawLogReader(RawFolder);
    }

    public static Case Create(string folder, string name, PlantConfig plant)
    {
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            throw new IOException($"案件資料夾已存在：{folder}");
        Directory.CreateDirectory(Path.Combine(folder, RawFolderName));
        var store = new SqliteEventStore(Path.Combine(folder, DatabaseFileName));
        var info = new CaseInfo(name, plant.Name, plant.ModuleOrder, plant.KnownLoggers, DateTimeOffset.Now);
        store.SaveInfo(info);
        return new Case(folder, store, info);
    }

    public static Case Open(string folder)
    {
        var db = Path.Combine(folder, DatabaseFileName);
        if (!File.Exists(db)) throw new FileNotFoundException("不是案件資料夾", db);
        var store = new SqliteEventStore(db);
        try
        {
            return new Case(folder, store, store.LoadInfo());
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    PlantConfig Plant => new() { Name = Info.PlantName, ModuleOrder = Info.ModuleOrder, KnownLoggers = Info.KnownLoggers };

    /// <summary>匯入一個「日期 → 模組」結構的資料夾。</summary>
    public ImportResult Import(string sourceFolder, CancellationToken ct = default)
    {
        _files = null;
        return CaseImporter.Import(sourceFolder, RawFolder, Plant, _store, ct);
    }

    public long EventCount => _store.GetEventCount();

    /// <summary>從指定事件序號起，依序號遞增讀取事件。</summary>
    public IReadOnlyList<LogEvent> GetEvents(long fromSequence, int maxCount) => _store.GetEvents(fromSequence, maxCount);

    public IReadOnlyList<StoredFile> GetFiles() => _store.GetFiles();

    StoredFile FindFile(string relativePath)
    {
        _files ??= _store.GetFiles().ToDictionary(f => f.RelativePath, StringComparer.Ordinal);
        return _files.TryGetValue(relativePath, out var f) ? f : throw new FileNotFoundException("案件中沒有這個檔案", relativePath);
    }

    /// <summary>原始檔複本的行數。</summary>
    public int GetRawLineCount(string relativePath) => _raw.LineCount(relativePath, FindFile(relativePath).Encoding);

    /// <summary>依原始行序讀取原始檔複本：第 firstLine 行起最多 count 行（ADR-0008「原始 LOG」面板）。</summary>
    public IReadOnlyList<RawLine> ReadRawLines(string relativePath, int firstLine, int count) =>
        _raw.Read(relativePath, FindFile(relativePath).Encoding, firstLine, count);

    public void Dispose() => _store.Dispose();
}
