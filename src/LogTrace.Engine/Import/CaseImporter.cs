using System.Diagnostics;
using System.Security.Cryptography;
using LogTrace.Engine.Events;
using LogTrace.Engine.Parsing;
using LogTrace.Engine.Plants;
using LogTrace.Engine.Storage;

namespace LogTrace.Engine.Import;

/// <summary>重複匯入時，來源中每個檔案的情況（ADR-0002 第 5 點）。</summary>
public enum ImportItemKind
{
    /// <summary>案件中沒有這個檔案：加入。</summary>
    New,
    /// <summary>雜湊相同：略過。</summary>
    Identical,
    /// <summary>案件中的檔案是新檔的開頭部分（例如晚一點重新下載）：自動改用新檔，原始行號不變。</summary>
    Extended,
    /// <summary>同名但內容衝突：由使用者選擇改用新檔或保留舊檔。</summary>
    Conflict,
}

/// <summary>來源中的一個檔案與它的情況。</summary>
/// <param name="Existing">案件中同一路徑目前參與查詢的版本；新檔案為 null。</param>
public sealed record ImportItem(SourceFile Source, ImportItemKind Kind, long Size, string Sha256, StoredFile? Existing)
{
    public string RelativePath => Source.RelativePath;
}

/// <summary>一次匯入的結果。</summary>
/// <param name="FileCount">來源中的檔案數。</param>
/// <param name="UnparsableFileCount">案件中整個檔案不符合共通格式、沒有產生事件的檔案數（ADR-0004 第 6 點）。</param>
public sealed record ImportResult(
    int FileCount, int NewFiles, int IdenticalFiles, int ExtendedFiles, int ConflictsUsedNew, int ConflictsKeptOld,
    long EventCount, int UnparsableFileCount, TimeSpan Elapsed);

/// <summary>
/// 已準備好、尚未套用的匯入：來源已複製或解開到暫存區，每個檔案都已分類。
/// 有衝突時，畫面先讓使用者選擇再套用；不套用時 Dispose 會清掉暫存區與壓縮檔複本。
/// </summary>
public sealed class PreparedImport : IDisposable
{
    internal PreparedImport(ImportRecord record, IReadOnlyList<ImportItem> items, string? stagingFolder, string? archiveCopyPath, Stopwatch clock)
    {
        Record = record;
        Items = items;
        StagingFolder = stagingFolder;
        ArchiveCopyPath = archiveCopyPath;
        Clock = clock;
    }

    public ImportRecord Record { get; }
    public IReadOnlyList<ImportItem> Items { get; }
    public IReadOnlyList<ImportItem> Conflicts => Items.Where(i => i.Kind == ImportItemKind.Conflict).ToList();

    internal string? StagingFolder { get; }
    internal string? ArchiveCopyPath { get; }
    internal Stopwatch Clock { get; }
    internal bool Applied { get; set; }

    public void Dispose()
    {
        if (StagingFolder is not null) CaseImporter.DeleteFolder(StagingFolder);
        if (!Applied && ArchiveCopyPath is not null && File.Exists(ArchiveCopyPath))
        {
            File.SetAttributes(ArchiveCopyPath, FileAttributes.Normal);
            File.Delete(ArchiveCopyPath);
        }
    }
}

/// <summary>
/// 匯入流程（ADR-0002、ADR-0004）：
/// <list type="number">
/// <item>準備：資料夾直接掃描；壓縮檔先整份複製到案件的 sources\，再解開到暫存區掃描。每個檔案計算雜湊並分類。</item>
/// <item>套用：新檔案與新版本放進 raw\（第 1 版放在相對路徑，之後的版本放在 _versions\&lt;版本&gt;\），以雜湊確認；
/// 舊版本永遠保留。之後重新解析所有參與查詢的檔案，依 ADR-0004 排序並重新編事件序號。</item>
/// </list>
/// 原始檔只讀不寫（CLAUDE.md 第 12 條）；案件內的複本設為唯讀。
/// </summary>
internal static class CaseImporter
{
    public const string SourcesFolderName = "sources";
    public const string StagingFolderName = "staging";
    public const string VersionsFolderName = "_versions";

    public static PreparedImport Prepare(string sourcePath, string caseFolder, IEventStore store, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var full = Path.GetFullPath(sourcePath);
        var id = store.NextImportId();
        string? staging = null, archiveCopy = null;
        ImportRecord record;
        string scanRoot;
        try
        {
            if (Directory.Exists(full))
            {
                record = new ImportRecord(id, DateTimeOffset.Now, full, "資料夾", null, null);
                scanRoot = full;
            }
            else if (File.Exists(full) && ArchiveExtractor.IsArchive(full))
            {
                var name = $"{id}-{Path.GetFileName(full)}";
                archiveCopy = Path.Combine(caseFolder, SourcesFolderName, name);
                var hash = CopyVerified(full, archiveCopy, HashFile(full));
                staging = Path.Combine(caseFolder, StagingFolderName, id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                DeleteFolder(staging);
                Directory.CreateDirectory(staging);
                ArchiveExtractor.Extract(full, staging, ct);
                record = new ImportRecord(id, DateTimeOffset.Now, full, Path.GetExtension(full).TrimStart('.').ToLowerInvariant(), hash, name);
                scanRoot = staging;
            }
            else if (File.Exists(full))
                throw new InvalidDataException($"只接受資料夾、7z 或 zip：{full}");
            else
                throw new DirectoryNotFoundException($"找不到資料夾或壓縮檔：{full}");

            var sources = LogFolderScanner.Scan(scanRoot);
            if (sources.Count == 0)
                throw new InvalidDataException("找不到「日期 → 模組」結構的 LOG（例如 20300107\\PLC-2030-01-07\\PLCLOG.txt）。");

            var active = store.GetFiles().Where(f => f.Active).ToDictionary(f => f.RelativePath, StringComparer.Ordinal);
            var rawFolder = Path.Combine(caseFolder, Cases.Case.RawFolderName);
            var items = new List<ImportItem>(sources.Count);
            foreach (var src in sources)
            {
                ct.ThrowIfCancellationRequested();
                var size = new FileInfo(src.FullPath).Length;
                var hash = HashFile(src.FullPath);
                active.TryGetValue(src.RelativePath, out var existing);
                var kind = existing switch
                {
                    null => ImportItemKind.New,
                    _ when existing.Sha256 == hash => ImportItemKind.Identical,
                    _ when existing.Size < size && IsPrefix(Path.Combine(rawFolder, existing.StoragePath), src.FullPath) => ImportItemKind.Extended,
                    _ => ImportItemKind.Conflict,
                };
                items.Add(new ImportItem(src, kind, size, hash, existing));
            }
            return new PreparedImport(record, items, staging, archiveCopy, clock);
        }
        catch
        {
            new PreparedImport(new ImportRecord(id, DateTimeOffset.Now, full, "", null, null), [], staging, archiveCopy, clock).Dispose();
            throw;
        }
    }

    /// <param name="useNewFor">衝突的檔案中，使用者選擇「改用新檔」的相對路徑；其餘衝突保留舊檔。</param>
    public static ImportResult Apply(PreparedImport prepared, IReadOnlySet<string> useNewFor, string caseFolder, PlantConfig plant,
        TimeCorrection correction, IEventStore store, CancellationToken ct)
    {
        var placed = new List<string>();
        try
        {
            return ApplyCore(prepared, useNewFor, caseFolder, plant, correction, store, placed, ct);
        }
        catch
        {
            // 沒有寫進案件資料庫的複本要移除，否則下次匯入會因為檔案已存在而失敗
            foreach (var f in placed.Where(File.Exists))
            {
                File.SetAttributes(f, FileAttributes.Normal);
                File.Delete(f);
            }
            throw;
        }
    }

    static ImportResult ApplyCore(PreparedImport prepared, IReadOnlySet<string> useNewFor, string caseFolder, PlantConfig plant,
        TimeCorrection correction, IEventStore store, List<string> placed, CancellationToken ct)
    {
        if (prepared.Applied) throw new InvalidOperationException("這次匯入已經套用過。");
        var rawFolder = Path.Combine(caseFolder, Cases.Case.RawFolderName);
        var record = prepared.Record;
        var files = store.GetFiles().ToList();
        var audit = new List<AuditEntry>();
        int added = 0, extended = 0, usedNew = 0, keptOld = 0;

        foreach (var item in prepared.Items)
        {
            ct.ThrowIfCancellationRequested();
            if (item.Kind == ImportItemKind.Identical) continue;
            bool activate = item.Kind switch
            {
                ImportItemKind.Conflict => useNewFor.Contains(item.RelativePath),
                _ => true,
            };
            int version = files.Where(f => f.RelativePath == item.RelativePath).Select(f => f.Version).DefaultIfEmpty(0).Max() + 1;
            var storage = version == 1 ? item.RelativePath : $"{VersionsFolderName}/{version}/{item.RelativePath}";
            var dest = Path.Combine(rawFolder, storage);
            Place(item, prepared.StagingFolder is not null, dest);
            placed.Add(dest);

            if (activate)
                for (int i = 0; i < files.Count; i++)
                    if (files[i].RelativePath == item.RelativePath && files[i].Active) files[i] = files[i] with { Active = false };

            var src = item.Source;
            var sourcePath = record.ArchiveCopy is null
                ? src.FullPath
                : $"{record.SourcePath}|{Path.GetRelativePath(prepared.StagingFolder!, src.FullPath).Replace('\\', '/')}";
            files.Add(new StoredFile(item.RelativePath, version, storage, activate, src.Module, src.DayFolder, src.Logger, src.StreamKey,
                item.Size, item.Sha256, "", 0, false, record.Id, sourcePath, record.Time));

            switch (item.Kind)
            {
                case ImportItemKind.New:
                    added++;
                    break;
                case ImportItemKind.Extended:
                    extended++;
                    audit.Add(AuditEntry.Now("重複匯入", item.RelativePath, $"舊檔是新檔的開頭部分，自動改用新檔（第 {version} 版）"));
                    break;
                case ImportItemKind.Conflict when activate:
                    usedNew++;
                    audit.Add(AuditEntry.Now("重複匯入的選擇", item.RelativePath, $"內容衝突，改用新檔（第 {version} 版），舊檔保留"));
                    break;
                case ImportItemKind.Conflict:
                    keptOld++;
                    audit.Add(AuditEntry.Now("重複匯入的選擇", item.RelativePath, $"內容衝突，保留舊檔；新檔另存為第 {version} 版"));
                    break;
            }
        }

        // 重新解析：參與查詢的檔案，以及這次新放進來的版本（記錄編碼與事件數）
        var events = Reparse(files, f => f.Active || f.ImportId == record.Id, rawFolder, plant, correction, ct);

        audit.Insert(0, AuditEntry.Now("匯入", record.SourcePath,
            $"{record.SourceKind}；{prepared.Items.Count} 個檔案：新增 {added}、相同略過 {prepared.Items.Count(i => i.Kind == ImportItemKind.Identical)}、" +
            $"自動改用較完整的新檔 {extended}、衝突改用新檔 {usedNew}、衝突保留舊檔 {keptOld}；事件 {events.Count} 筆"));
        var (fields, effects) = new FieldExtractor(plant.ParseRules).ExtractAllWithEffects(events);
        store.SaveImport(record, files, events, fields, effects, audit);
        prepared.Applied = true;

        return new ImportResult(prepared.Items.Count, added, prepared.Items.Count(i => i.Kind == ImportItemKind.Identical), extended,
            usedNew, keptOld, events.Count, files.Count(f => f.Active && f.Unparsable), prepared.Clock.Elapsed);
    }

    /// <summary>
    /// 依廠別設定解析 files 中符合 include 的檔案（更新其編碼、事件數、無法解析），參與查詢的檔案依 ADR-0004 排序並編事件序號。
    /// files 會被就地更新。
    /// </summary>
    static List<LogEvent> Reparse(List<StoredFile> files, Func<StoredFile, bool> include, string rawFolder, PlantConfig plant,
        TimeCorrection correction, CancellationToken ct)
    {
        var parsed = new List<(StoredFile File, IReadOnlyList<ParsedEvent> Events)>();
        for (int i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var f = files[i];
            if (!include(f)) continue;
            // 廠別設定有指定編碼時以指定為準，否則自動判斷（O-002）
            var bytes = File.ReadAllBytes(Path.Combine(rawFolder, f.StoragePath));
            var result = LogRecordParser.Parse(bytes, plant.EncodingFor(f.Module, f.Logger) ?? LogEncoding.Detect(bytes));
            files[i] = f = f with { Encoding = result.Encoding, EventCount = result.Events.Count, Unparsable = result.Unparsable };
            if (f.Active) parsed.Add((f, result.Events));
        }
        return Sequence(parsed, plant, correction);
    }

    /// <summary>
    /// 以廠別設定與時間偏移量重建案件的事件（套用新版廠別設定，ADR-0006 第 8 點；修改偏移量，ADR-0003 第 5 點）：
    /// 重新解碼、排序、編事件序號，重新抽出解析欄位與效果。原始檔複本不變；穩定識別碼不變，事件序號可能改變。
    /// </summary>
    public static (List<StoredFile> Files, List<LogEvent> Events, List<EventField> Fields, List<EventEffect> Effects) Rebuild(
        string caseFolder, PlantConfig plant, TimeCorrection correction, IEventStore store, CancellationToken ct)
    {
        var files = store.GetFiles().ToList();
        var events = Reparse(files, f => f.Active, Path.Combine(caseFolder, Cases.Case.RawFolderName), plant, correction, ct);
        var (fields, effects) = new FieldExtractor(plant.ParseRules).ExtractAllWithEffects(events);
        return (files, events, fields, effects);
    }

    /// <summary>把來源檔放進案件：壓縮檔解開的暫存檔直接搬移，資料夾來源則複製。兩者都以準備時的雜湊確認。</summary>
    static void Place(ImportItem item, bool fromStaging, string dest)
    {
        if (File.Exists(dest)) throw new IOException($"案件中已經有這個檔案，不覆寫：{dest}");
        if (fromStaging)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Move(item.Source.FullPath, dest);
            if (HashFile(dest) != item.Sha256) throw new IOException($"放進案件後的雜湊與來源不同：{item.RelativePath}");
            File.SetAttributes(dest, File.GetAttributes(dest) | FileAttributes.ReadOnly);
        }
        else CopyVerified(item.Source.FullPath, dest, item.Sha256);
    }

    /// <summary>複製檔案，並確認複本的雜湊與 expectedHash 相同（來源在準備之後被改動時也會發現）。複本設為唯讀。</summary>
    static string CopyVerified(string source, string dest, string expectedHash)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(source, dest, overwrite: false);
        var after = HashFile(dest);
        if (after != expectedHash)
        {
            File.Delete(dest);
            throw new IOException($"複製後的雜湊與來源不同：{source}");
        }
        File.SetAttributes(dest, File.GetAttributes(dest) | FileAttributes.ReadOnly);
        return after;
    }

    static string HashFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    /// <summary>prefixPath 的內容是否為 fullPath 的開頭部分。</summary>
    static bool IsPrefix(string prefixPath, string fullPath)
    {
        using var a = new FileStream(prefixPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var b = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (a.Length > b.Length) return false;
        var bufA = new byte[81920];
        var bufB = new byte[81920];
        int n;
        while ((n = a.Read(bufA)) > 0)
        {
            b.ReadExactly(bufB, 0, n);
            if (!bufA.AsSpan(0, n).SequenceEqual(bufB.AsSpan(0, n))) return false;
        }
        return true;
    }

    internal static void DeleteFolder(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(folder, recursive: true);
    }

    /// <summary>
    /// ADR-0004 第 3、5 點：依標準化時間排序；時間相同時，同一檔案依行號（輪替檔封存在前），
    /// 不同檔案依模組固定順序、再依 Logger 名稱。
    /// 同一 Logger 的封存檔與現行檔視為一條連續的紀錄：「時間倒退」與「時間為沿用」都跨檔案判斷。
    /// 時間無效的事件沿用前一筆有效事件的時間；前面沒有有效事件時（檔案開頭的前置內容），
    /// 改用後面第一筆有效事件的時間，依行號排在它之前；整條紀錄都沒有有效時間時，用日期資料夾的 00:00。
    /// 時間偏移量（ADR-0003 第 5 點）：排序與顯示使用校正後的時間（加上該模組的偏移量），偏移量不為 0 時標示「已校正」。
    /// 「時間倒退」以 LOG 記錄的時間判斷（同一條紀錄的偏移量相同，結果不變）；「同時間戳記」以校正後的時間判斷。
    /// </summary>
    static List<LogEvent> Sequence(List<(StoredFile File, IReadOnlyList<ParsedEvent> Events)> parsed, PlantConfig plant, TimeCorrection correction)
    {
        var rows = new List<(DateTime Time, int ModuleRank, StoredFile File, ParsedEvent Event, EventFlags Flags)>();
        foreach (var stream in parsed.GroupBy(p => (p.File.DayFolder, p.File.Module, p.File.Logger)))
        {
            var all = stream.OrderBy(p => p.File.StreamKey, StringComparer.Ordinal)
                .SelectMany(p => p.Events.Select(e => (p.File, Event: e))).ToList();
            DateTime lastValid = all.Where(x => (x.Event.Flags & EventFlags.TimeInvalid) == 0).Select(x => (DateTime?)x.Event.Timestamp).FirstOrDefault()
                ?? DayStart(stream.Key.DayFolder);
            bool sawValid = false;
            var offset = correction.For(stream.Key.Module);
            foreach (var (file, e) in all)
            {
                var flags = e.Flags;
                DateTime time;
                if ((flags & EventFlags.TimeInvalid) == 0)
                {
                    if (sawValid && e.Timestamp < lastValid) flags |= EventFlags.TimeBackward;
                    lastValid = time = e.Timestamp;
                    sawValid = true;
                }
                else time = lastValid;
                if (!plant.IsKnownLogger(file.Logger)) flags |= EventFlags.UnknownKind;
                if (offset != TimeSpan.Zero) flags |= EventFlags.Corrected;
                rows.Add((time + offset, plant.ModuleRank(file.Module), file, e, flags));
            }
        }

        var ordered = rows
            .OrderBy(r => r.Time)
            .ThenBy(r => r.ModuleRank)
            .ThenBy(r => r.File.Module, StringComparer.Ordinal)
            .ThenBy(r => r.File.Logger, StringComparer.Ordinal)
            .ThenBy(r => r.File.DayFolder, StringComparer.Ordinal)
            .ThenBy(r => r.File.StreamKey, StringComparer.Ordinal)
            .ThenBy(r => r.Event.LineStart)
            .ToList();

        // 「同時間戳記」只看有效的時間：時間為沿用的事件不算，避免把沿用的時間誤當成同時發生
        var validPerTime = ordered.Where(r => (r.Flags & EventFlags.TimeInvalid) == 0)
            .GroupBy(r => r.Time).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();

        var result = new List<LogEvent>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
        {
            var (time, _, file, e, flags) = ordered[i];
            if ((flags & EventFlags.TimeInvalid) == 0 && validPerTime.Contains(time))
                flags |= EventFlags.SameTimestamp;
            result.Add(new LogEvent(
                Sequence: i + 1,
                Id: new StableEventId(file.Module, file.DayFolder, file.RelativePath, e.LineStart),
                Timestamp: time,
                RawTimestamp: e.RawTimestamp,
                Level: e.Level,
                Module: file.Module,
                Logger: e.Logger.Length > 0 ? e.Logger : file.Logger,
                Message: e.Message,
                Source: new EventSource(file.RelativePath, e.LineStart, e.LineEnd, e.ByteOffset, e.ByteLength),
                Flags: flags));
        }
        return result;
    }

    static DateTime DayStart(string dayFolder) =>
        DateTime.TryParseExact(dayFolder, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var d) ? d : DateTime.MinValue;
}
