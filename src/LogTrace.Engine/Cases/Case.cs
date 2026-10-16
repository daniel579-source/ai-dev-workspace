using LogTrace.Engine.Events;
using LogTrace.Engine.Import;
using LogTrace.Engine.Parsing;
using LogTrace.Engine.Plants;
using LogTrace.Engine.Storage;

namespace LogTrace.Engine.Cases;

/// <summary>
/// 案件：一次分析的工作單位（GLOSSARY）。案件資料夾的結構（ADR-0011 第 2 點）：
/// <list type="bullet">
/// <item>case.db（不可重建）：事件、檔案版本、匯入紀錄、稽核紀錄。</item>
/// <item>raw\（不可重建）：原始檔複本；重複匯入的其他版本在 raw\_versions\ 下。</item>
/// <item>sources\（不可重建）：匯入的原始壓縮檔（ADR-0002 第 4 點）。</item>
/// <item>說明檔（ADR-0009 第 4、6 點）。index\（可重建）在後續 ticket 加入。</item>
/// </list>
/// 關閉（Dispose）後不留下連線與暫存檔，整個資料夾可以直接複製。
/// </summary>
public sealed class Case : IDisposable
{
    public const string DatabaseFileName = "case.db";
    public const string RawFolderName = "raw";
    public const string ReadmeFileName = "請先讀我.txt";

    const string ReadmeText = """
        這是 LogTrace 的案件資料夾。

        ・本資料夾含有真實的設備 LOG，請勿外流，也不要上傳到未經核准的外部服務。
        ・備份：先在 LogTrace 中關閉這個案件（或關閉程式），再複製整個資料夾。案件開啟時複製，可能複製到不完整的資料。
        ・不要修改或刪除資料夾內的檔案；要刪除案件，請在 LogTrace 中使用「刪除案件」。
        ・case.db：事件與匯入紀錄。raw\：原始 LOG 的複本。sources\：匯入的原始壓縮檔。
        """;

    readonly IEventStore _store;
    readonly RawLogReader _raw;
    Dictionary<string, StoredFile>? _activeFiles;

    public string Folder { get; }
    public CaseInfo Info { get; private set; }
    public string RawFolder => Path.Combine(Folder, RawFolderName);

    Case(string folder, IEventStore store, CaseInfo info)
    {
        Folder = folder;
        _store = store;
        Info = info;
        _raw = new RawLogReader(RawFolder);
    }

    /// <param name="snapshot">建立案件時所用的廠別設定檔（含 Layout）；保存為快照（ADR-0006 第 8 點）。內建預設時為 null。</param>
    /// <param name="snapshotSource">廠別設定檔的路徑，用來檢查之後是否有新版。</param>
    public static Case Create(string folder, string name, PlantConfig plant, PlantConfigDocument? snapshot = null, string? snapshotSource = null)
    {
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            throw new IOException($"案件資料夾已存在：{folder}");
        Directory.CreateDirectory(Path.Combine(folder, RawFolderName));
        File.WriteAllText(Path.Combine(folder, ReadmeFileName), ReadmeText.ReplaceLineEndings("\r\n"), System.Text.Encoding.UTF8);
        var store = new SqliteEventStore(Path.Combine(folder, DatabaseFileName));
        var info = new CaseInfo(name, plant, DateTimeOffset.Now);
        store.SaveInfo(info);
        if (snapshot is not null) store.SavePlantSnapshot(PlantConfigFile.Serialize(snapshot), snapshotSource);
        store.AddAudit(AuditEntry.Now("建立案件", name, $"廠別：{plant.Name}（修訂 {plant.Revision}）；位置：{folder}"));
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

    PlantConfig Plant => Info.Plant;

    /// <summary>準備匯入一個資料夾、7z 或 zip（「日期 → 模組」結構，可以多天）：分類每個檔案，尚未寫入案件。</summary>
    public PreparedImport PrepareImport(string source, CancellationToken ct = default) =>
        CaseImporter.Prepare(source, Folder, _store, ct);

    /// <summary>套用準備好的匯入。</summary>
    /// <param name="useNewFor">內容衝突的檔案中，要改用新檔的相對路徑；其餘衝突保留舊檔（兩者都留在案件內）。</param>
    public ImportResult ApplyImport(PreparedImport prepared, IReadOnlySet<string>? useNewFor = null, CancellationToken ct = default)
    {
        _activeFiles = null;
        return CaseImporter.Apply(prepared, useNewFor ?? new HashSet<string>(), Folder, Plant, Info.Correction, _store, ct);
    }

    /// <summary>準備並套用匯入；內容衝突的檔案一律保留舊檔。</summary>
    public ImportResult Import(string source, CancellationToken ct = default)
    {
        using var prepared = PrepareImport(source, ct);
        return ApplyImport(prepared, null, ct);
    }

    public long EventCount => _store.GetEventCount();

    /// <summary>從指定事件序號起，依序號遞增讀取事件。</summary>
    public IReadOnlyList<LogEvent> GetEvents(long fromSequence, int maxCount) => _store.GetEvents(fromSequence, maxCount);

    /// <summary>所有檔案版本（含不參與查詢的舊版本）。</summary>
    public IReadOnlyList<StoredFile> GetFiles() => _store.GetFiles();

    public IReadOnlyList<ImportRecord> GetImports() => _store.GetImports();

    /// <summary>稽核紀錄（ADR-0009 第 5 點）。</summary>
    public IReadOnlyList<AuditEntry> GetAudit() => _store.GetAudit();

    StoredFile FindActiveFile(string relativePath)
    {
        _activeFiles ??= _store.GetFiles().Where(f => f.Active).ToDictionary(f => f.RelativePath, StringComparer.Ordinal);
        return _activeFiles.TryGetValue(relativePath, out var f) ? f : throw new FileNotFoundException("案件中沒有這個檔案", relativePath);
    }

    /// <summary>原始檔複本（參與查詢的版本）的行數。</summary>
    public int GetRawLineCount(string relativePath)
    {
        var f = FindActiveFile(relativePath);
        return _raw.LineCount(f.StoragePath, f.Encoding);
    }

    /// <summary>依原始行序讀取原始檔複本（參與查詢的版本）：第 firstLine 行起最多 count 行（ADR-0008「原始 LOG」面板）。</summary>
    public IReadOnlyList<RawLine> ReadRawLines(string relativePath, int firstLine, int count)
    {
        var f = FindActiveFile(relativePath);
        return _raw.Read(f.StoragePath, f.Encoding, firstLine, count);
    }

    // ---- 解析欄位（ADR-0005 第 8 點） ----

    /// <summary>一筆事件的解析欄位。</summary>
    public IReadOnlyList<EventField> GetFields(long sequence) => _store.GetFields(sequence);

    /// <summary>
    /// 案件中出現的站點、搬送機、對接設備編號與出現的事件數（ADR-0006 第 3 點「未對應的編號」的來源）。
    /// </summary>
    public IReadOnlyList<(string Kind, string Value, long Count)> GetIdentifierCounts() =>
        _store.GetFieldValueCounts(FieldKinds.Identifiers);

    /// <summary>事件序號範圍內（含頭尾）出現的站點、搬送機、對接設備編號與事件數（回放畫面的「未對應」清單）。</summary>
    public IReadOnlyList<(string Kind, string Value, long Count)> GetIdentifierCounts(long fromSequence, long toSequence) =>
        _store.GetFieldValueCounts(FieldKinds.Identifiers, fromSequence, toSequence);

    /// <summary>案件中出現的命令編號與事件數（命令清單）。</summary>
    public IReadOnlyList<(string Command, long Count)> GetCommandCounts() =>
        _store.GetFieldValueCounts([FieldKinds.Command]).Select(x => (x.Value, x.Count)).ToList();

    /// <summary>
    /// 依解析欄位篩選事件，傳回事件序號（依時間排序）。kinds 中任一種欄位的值等於 values 中任一值（不分大小寫）即符合；
    /// 例如篩選 Layout 元素時，kinds 為站點、搬送機、對接設備，values 為元素的所有別名。
    /// </summary>
    public IReadOnlyList<long> FindSequences(IReadOnlyList<string> kinds, IReadOnlyList<string> values) =>
        _store.FindSequences(kinds, values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList());

    /// <summary>
    /// 以新的抽取規則重新解析所有事件的欄位（例如修改了廠別設定的規則），並把規則存進案件、寫入稽核紀錄。
    /// 事件與排序不變。
    /// </summary>
    public int ReparseFields(IReadOnlyList<ParseRule> rules, CancellationToken ct = default)
    {
        foreach (var rule in rules)
            if (FieldExtractor.Check(rule) is { } problem) throw new ArgumentException(problem, nameof(rules));
        var extractor = new FieldExtractor(rules);
        var fields = new List<EventField>();
        var effects = new List<EventEffect>();
        long next = 1;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = _store.GetEvents(next, 10_000);
            if (page.Count == 0) break;
            var (f, x) = extractor.ExtractAllWithEffects(page);
            fields.AddRange(f);
            effects.AddRange(x);
            next = page[^1].Sequence + 1;
        }
        var info = Info with { Plant = Info.Plant with { ParseRules = [.. rules] } };
        _store.ReplaceFields(fields, effects, info, AuditEntry.Now("重新解析欄位", Info.Name, $"{rules.Count} 條規則；{fields.Count} 個欄位值"));
        Info = info;
        return fields.Count;
    }

    /// <summary>
    /// 搜尋（ADR-0010）：傳回符合的事件序號，依時間排序（與事件清單相同）。
    /// 之前建立的案件第一次搜尋時，先補上搜尋用文字。
    /// </summary>
    public IReadOnlyList<long> Search(Search.SearchQuery query, CancellationToken ct = default)
    {
        _store.EnsureSearchText();
        return _store.Search(query, ct);
    }

    // ---- 廠別設定的快照與套用新版（ADR-0006 第 8 點） ----

    /// <summary>
    /// 建立案件時所用廠別設定檔的快照（含 Layout）。案件一律以快照開啟，廠別設定檔被修改或刪除都不影響。
    /// 內建預設、或快照功能加入前建立的案件為 null。
    /// </summary>
    public PlantConfigDocument? PlantSnapshot => _store.LoadPlantSnapshot().Json is { } json ? PlantConfigFile.Deserialize(json) : null;

    /// <summary>快照的來源檔案路徑。</summary>
    public string? PlantSnapshotSource => _store.LoadPlantSnapshot().SourcePath;

    /// <summary>
    /// 找出比案件所用版本新的廠別設定檔：先看快照的來源路徑，再依廠別名稱找存放位置下的設定檔。
    /// 讀不了的檔案略過；內建預設的案件沒有新版。
    /// </summary>
    public (PlantConfigDocument Document, string Path)? FindNewerPlant(PlantConfigLibrary library)
    {
        if (Info.Plant.Revision == 0 && PlantSnapshot is null) return null;
        var candidates = new List<string>();
        if (PlantSnapshotSource is { } source && File.Exists(source)) candidates.Add(source);
        candidates.AddRange(library.List().Where(p => !candidates.Contains(p, StringComparer.OrdinalIgnoreCase)));
        foreach (var path in candidates)
        {
            try
            {
                var doc = PlantConfigFile.Load(path);
                if (string.Equals(doc.Plant, Info.Plant.Name, StringComparison.Ordinal) && doc.Revision > Info.Plant.Revision) return (doc, path);
            }
            catch (Exception ex) when (ex is PlantConfigFormatException or IOException or UnauthorizedAccessException)
            {
                // 讀不了的設定檔略過
            }
        }
        return null;
    }

    /// <summary>
    /// 套用新版的廠別設定：以新的設定重建事件（解碼、排序、解析欄位與效果），更新快照，並寫入稽核紀錄（舊修訂號、新修訂號、時間）。
    /// 原始檔複本不變；穩定識別碼不變，事件序號可能改變。
    /// </summary>
    public void ApplyPlant(PlantConfigDocument document, string? sourcePath, CancellationToken ct = default)
    {
        var plant = document.ToPlantConfig();
        foreach (var rule in plant.ParseRules)
            if (FieldExtractor.Check(rule) is { } problem) throw new ArgumentException(problem, nameof(document));
        var (files, events, fields, effects) = CaseImporter.Rebuild(Folder, plant, Info.Correction, _store, ct);
        var info = Info with { Plant = plant };
        _store.Rebuild(files, events, fields, effects, info, PlantConfigFile.Serialize(document), sourcePath,
            AuditEntry.Now("套用新版廠別設定", plant.Name, $"修訂 {Info.Plant.Revision} → {plant.Revision}；事件 {events.Count} 筆"));
        Info = info;
        _activeFiles = null;
    }

    /// <summary>
    /// 設定時間偏移量（ADR-0003 第 5 點）：以新的偏移量重新排序並編事件序號，寫入稽核紀錄（舊值、新值、時間、修改人）。
    /// 原始檔複本與原始時間字串不變；改回 0 即完全恢復。偏移量相同時不做任何事，回傳 false。
    /// </summary>
    public bool SetTimeCorrection(TimeCorrection correction, CancellationToken ct = default)
    {
        if (correction.Equals(Info.Correction)) return false;
        var (files, events, fields, effects) = CaseImporter.Rebuild(Folder, Plant, correction, _store, ct);
        var info = Info with { Correction = correction };
        _store.Rebuild(files, events, fields, effects, info, null, null,
            AuditEntry.Now("設定時間偏移量", Info.Name, $"{Info.Correction.Describe()} → {correction.Describe()}；事件 {events.Count} 筆"));
        Info = info;
        _activeFiles = null;
        return true;
    }

    /// <summary>所有效果，依事件序號（世界模型的輸入，ADR-0005）。</summary>
    public IReadOnlyList<EventEffect> GetEffects() => _store.GetEffects();

    public void Dispose() => _store.Dispose();
}
