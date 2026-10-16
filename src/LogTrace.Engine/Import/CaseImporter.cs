using System.Security.Cryptography;
using LogTrace.Engine.Events;
using LogTrace.Engine.Parsing;
using LogTrace.Engine.Plants;
using LogTrace.Engine.Storage;

namespace LogTrace.Engine.Import;

/// <summary>一次匯入的結果。</summary>
/// <param name="UnparsableFileCount">整個檔案不符合共通格式、沒有產生事件的檔案數（ADR-0004 第 6 點）。</param>
public sealed record ImportResult(int FileCount, long EventCount, int UnparsableFileCount);

/// <summary>
/// 匯入流程：辨識結構 → 原始檔複製進案件並以雜湊確認 → 解析 → 依 ADR-0004 排序並編事件序號 → 儲存。
/// 原始檔只讀不寫（CLAUDE.md 第 12 條）；解析的對象是案件內的複本。
/// </summary>
internal static class CaseImporter
{
    public static ImportResult Import(string sourceFolder, string rawFolder, PlantConfig plant, IEventStore store, CancellationToken ct)
    {
        if (store.GetEventCount() > 0)
            throw new InvalidOperationException("這個案件已經匯入過 LOG；重複匯入尚未支援。");

        var sources = LogFolderScanner.Scan(sourceFolder);
        if (sources.Count == 0)
            throw new InvalidDataException("找不到「日期 → 模組」結構的 LOG（例如 20300107\\PLC-2030-01-07\\PLCLOG.txt）。");

        var stored = new List<StoredFile>(sources.Count);
        var parsed = new List<(SourceFile File, IReadOnlyList<ParsedEvent> Events)>(sources.Count);
        foreach (var src in sources)
        {
            ct.ThrowIfCancellationRequested();
            var dest = Path.Combine(rawFolder, src.RelativePath);
            var (size, hash) = CopyVerified(src, dest);
            var result = LogRecordParser.Parse(File.ReadAllBytes(dest));
            stored.Add(new StoredFile(src.Module, src.DayFolder, src.Logger, src.RelativePath, size, hash,
                result.Encoding, result.Events.Count, result.Unparsable));
            parsed.Add((src, result.Events));
        }

        var events = Sequence(parsed, plant);
        store.SaveImport(stored, events);
        return new ImportResult(stored.Count, events.Count, stored.Count(f => f.Unparsable));
    }

    /// <summary>複製原始檔，並確認複本與原始檔的雜湊相同。</summary>
    static (long Size, string Sha256) CopyVerified(SourceFile src, string dest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var before = HashFile(src.FullPath);
        File.Copy(src.FullPath, dest, overwrite: false);
        var after = HashFile(dest);
        if (before != after)
            throw new IOException($"複製後的雜湊與原始檔不同：{src.RelativePath}");
        File.SetAttributes(dest, File.GetAttributes(dest) | FileAttributes.ReadOnly);
        return (new FileInfo(dest).Length, before);
    }

    static string HashFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    /// <summary>
    /// ADR-0004 第 3、5 點：依標準化時間排序；時間相同時，同一檔案依行號（輪替檔封存在前），
    /// 不同檔案依模組固定順序、再依 Logger 名稱。
    /// 同一 Logger 的封存檔與現行檔視為一條連續的紀錄：「時間倒退」與「時間為沿用」都跨檔案判斷。
    /// 時間無效的事件沿用前一筆有效事件的時間；前面沒有有效事件時（檔案開頭的前置內容），
    /// 改用後面第一筆有效事件的時間，依行號排在它之前；整條紀錄都沒有有效時間時，用日期資料夾的 00:00。
    /// </summary>
    static List<LogEvent> Sequence(List<(SourceFile File, IReadOnlyList<ParsedEvent> Events)> parsed, PlantConfig plant)
    {
        var rows = new List<(DateTime Time, int ModuleRank, string Module, string Logger, int StreamOrder, SourceFile File, ParsedEvent Event, EventFlags Flags)>();
        foreach (var stream in parsed.GroupBy(p => (p.File.DayFolder, p.File.Module, p.File.Logger)))
        {
            var files = stream.OrderBy(p => p.File.StreamOrder).ToList();
            var all = files.SelectMany(p => p.Events.Select(e => (p.File, Event: e))).ToList();
            DateTime? lastValid = all.Where(x => (x.Event.Flags & EventFlags.TimeInvalid) == 0).Select(x => (DateTime?)x.Event.Timestamp).FirstOrDefault()
                ?? DayStart(stream.Key.DayFolder);
            bool sawValid = false;
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
                else time = lastValid.Value;
                if (!plant.IsKnownLogger(file.Logger)) flags |= EventFlags.UnknownKind;
                rows.Add((time, plant.ModuleRank(file.Module), file.Module, file.Logger, file.StreamOrder, file, e, flags));
            }
        }

        var ordered = rows
            .OrderBy(r => r.Time)
            .ThenBy(r => r.ModuleRank)
            .ThenBy(r => r.Module, StringComparer.Ordinal)
            .ThenBy(r => r.Logger, StringComparer.Ordinal)
            .ThenBy(r => r.StreamOrder)
            .ThenBy(r => r.Event.LineStart)
            .ToList();

        // 「同時間戳記」只看有效的時間：時間為沿用的事件不算，避免把沿用的時間誤當成同時發生
        var validPerTime = ordered.Where(r => (r.Flags & EventFlags.TimeInvalid) == 0)
            .GroupBy(r => r.Time).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();

        var result = new List<LogEvent>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
        {
            var (time, _, module, logger, _, file, e, flags) = ordered[i];
            if ((flags & EventFlags.TimeInvalid) == 0 && validPerTime.Contains(time))
                flags |= EventFlags.SameTimestamp;
            result.Add(new LogEvent(
                Sequence: i + 1,
                Id: new StableEventId(module, file.DayFolder, file.RelativePath, e.LineStart),
                Timestamp: time,
                RawTimestamp: e.RawTimestamp,
                Level: e.Level,
                Module: module,
                Logger: e.Logger.Length > 0 ? e.Logger : logger,
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
