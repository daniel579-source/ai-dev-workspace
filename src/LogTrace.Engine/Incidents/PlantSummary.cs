using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using LogTrace.Engine.Search;

namespace LogTrace.Engine.Incidents;

/// <summary>同一項目中的一種處置：文字（取第一次出現的寫法）、次數與紀錄編號。</summary>
public sealed record SummaryDisposal(string Text, int Count, IReadOnlyList<string> RecordIds);

/// <summary>排名的一項：警報代碼、設備或異常類型，次數（紀錄筆數）、紀錄編號與處置。</summary>
public sealed record SummaryItem(string Key, int Count, IReadOnlyList<string> RecordIds, IReadOnlyList<SummaryDisposal> Disposals);

/// <summary>總結涵蓋的紀錄與當時的版次（之後新增或改版的已審核紀錄由此判斷）。</summary>
public sealed record SummaryCoverage(string Id, int Rev);

/// <summary>
/// 一份總結的內容（ADR-0023 第 1 點，#168）：產生時的統計與工程師寫的說明。
/// 統計由程式產生（不用 AI）；異常類型由警報代碼是否為空推得。
/// </summary>
public sealed record SummaryContent
{
    public DateTimeOffset GeneratedAt { get; init; }
    public string GeneratedBy { get; init; } = "";
    public IReadOnlyList<SummaryCoverage> Covered { get; init; } = [];
    public IReadOnlyList<SummaryItem> ByAlarmCode { get; init; } = [];
    public IReadOnlyList<SummaryItem> ByDevice { get; init; } = [];
    public IReadOnlyList<SummaryItem> ByKind { get; init; } = [];
    /// <summary>工程師寫的說明（整份一段，使用者決定）。</summary>
    public string Note { get; init; } = "";
}

/// <summary>
/// 每廠的總結檔（<c>&lt;廠別設定檔名&gt;.summary.json</c>，放在廠別設定檔旁，不進 repo）：
/// 目前有效的一份（已審核）與審核中的草稿。只保留最新一份已審核的總結；草稿審核通過後取代它（ADR-0023 第 1 點，Jev 0.86）。
/// </summary>
public sealed record PlantSummaryFile
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public string Plant { get; init; } = "";
    public SummaryContent? Reviewed { get; init; }
    public string? Reviewer { get; init; }
    public DateTimeOffset? ReviewedAt { get; init; }
    public IReadOnlyList<string> ReviewWarnings { get; init; } = [];
    public SummaryContent? Draft { get; init; }
}

/// <summary>每廠總結的產生、說明、審核與新舊判斷（ADR-0023 第 1 點，#168）。</summary>
public static class PlantSummaries
{
    public const string KindAlarm = "有警報代碼", KindStall = "卡住（沒有代碼）";

    /// <summary>異常類型（推得）：有警報代碼為「有警報代碼」，沒有為「卡住（沒有代碼）」。</summary>
    public static string KindOf(IncidentContent c) => c.AlarmCodes.Any(a => !string.IsNullOrWhiteSpace(a)) ? KindAlarm : KindStall;

    /// <summary>
    /// 依目前有效的紀錄（已審核，或修訂中的舊版）產生統計；草稿、停用與已過時不列入。
    /// 排名依筆數由多到少，同筆數依名稱。
    /// </summary>
    public static SummaryContent Build(IEnumerable<IncidentRecord> records, string user, DateTimeOffset now, string note = "")
    {
        var effective = records.Select(r => (r.Id, V: IncidentWorkflow.Effective(r))).Where(x => x.V is not null)
            .Select(x => (x.Id, x.V!.Rev, C: x.V.Content)).OrderBy(x => x.Id, StringComparer.Ordinal).ToList();
        IReadOnlyList<SummaryItem> Rank(Func<IncidentContent, IEnumerable<string>> keys) =>
            effective.SelectMany(x => keys(x.C).Select(k => k.Trim()).Where(k => k.Length > 0).Distinct().Select(k => (Key: k, x.Id, x.C)))
                .GroupBy(x => x.Key, StringComparer.Ordinal)
                .Select(g => new SummaryItem(g.Key, g.Count(), g.Select(x => x.Id).ToList(), Disposals(g.Select(x => (x.Id, x.C)))))
                .OrderByDescending(i => i.Count).ThenBy(i => i.Key, StringComparer.Ordinal).ToList();
        return new()
        {
            GeneratedAt = now,
            GeneratedBy = user.Trim(),
            Covered = effective.Select(x => new SummaryCoverage(x.Id, x.Rev)).ToList(),
            ByAlarmCode = Rank(c => c.AlarmCodes),
            ByDevice = Rank(c => c.Devices),
            ByKind = Rank(c => [KindOf(c)]),
            Note = note,
        };
    }

    /// <summary>處置依文字歸類（不分大小寫、全形半形與前後空白），依筆數由多到少。</summary>
    static IReadOnlyList<SummaryDisposal> Disposals(IEnumerable<(string Id, IncidentContent C)> items) =>
        items.Where(x => x.C.Disposal.Trim().Length > 0)
            .GroupBy(x => TextNormalizer.Normalize(x.C.Disposal.Trim()), StringComparer.Ordinal)
            .Select(g => new SummaryDisposal(g.First().C.Disposal.Trim(), g.Count(), g.Select(x => x.Id).ToList()))
            .OrderByDescending(d => d.Count).ThenBy(d => d.Text, StringComparer.Ordinal).ToList();

    /// <summary>產生新的草稿（取代原本的草稿）；說明沿用原本的草稿或目前有效的總結，方便修改。</summary>
    public static PlantSummaryFile Generate(PlantSummaryFile? file, string plant, IEnumerable<IncidentRecord> records, string user, DateTimeOffset now)
    {
        var note = file?.Draft?.Note ?? file?.Reviewed?.Note ?? "";
        return (file ?? new PlantSummaryFile()) with { Plant = plant, Draft = Build(records, user, now, note) };
    }

    public static PlantSummaryFile SaveNote(PlantSummaryFile file, string note) =>
        file.Draft is { } d ? file with { Draft = d with { Note = note.Trim() } } : throw new InvalidOperationException("沒有總結草稿");

    /// <summary>審核草稿時的提醒（確認後仍可通過）：說明空白、產生者與審核者同一人。審核者空白時不能通過。</summary>
    public static IReadOnlyList<string> Warnings(PlantSummaryFile file, string reviewer)
    {
        var d = file.Draft ?? throw new InvalidOperationException("沒有總結草稿");
        var w = new List<string>();
        if (d.Note.Trim().Length == 0) w.Add("說明空白");
        if (string.Equals(d.GeneratedBy, reviewer.Trim(), StringComparison.Ordinal)) w.Add("產生者與審核者同一人");
        return w;
    }

    /// <summary>審核通過：草稿成為目前有效的總結，取代舊的（只保留最新一份）。</summary>
    public static PlantSummaryFile Approve(PlantSummaryFile file, string reviewer, DateTimeOffset now)
    {
        if (file.Draft is null) throw new InvalidOperationException("沒有總結草稿");
        if (string.IsNullOrWhiteSpace(reviewer)) throw new InvalidOperationException("請填寫審核者");
        return file with { Reviewed = file.Draft, Reviewer = reviewer.Trim(), ReviewedAt = now, ReviewWarnings = Warnings(file, reviewer), Draft = null };
    }

    public static PlantSummaryFile DiscardDraft(PlantSummaryFile file) => file with { Draft = null };

    /// <summary>
    /// 總結之後新增或改版的已審核紀錄筆數：目前有效、但不在涵蓋清單中（或版次較新）的紀錄。
    /// 涵蓋的紀錄之後被停用或標示已過時不計入，也不另外提示（Jev 0.25）。
    /// </summary>
    public static int NewSince(SummaryContent s, IEnumerable<IncidentRecord> records)
    {
        var covered = s.Covered.ToDictionary(c => c.Id, c => c.Rev, StringComparer.Ordinal);
        return records.Count(r => IncidentWorkflow.Effective(r) is { } v && (!covered.TryGetValue(r.Id, out var rev) || v.Rev > rev));
    }

    /// <summary>經過時間的文字：「3 天」「5 小時」「12 分鐘」。</summary>
    public static string AgeText(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays} 天" : t.TotalHours >= 1 ? $"{(int)t.TotalHours} 小時" : $"{Math.Max(0, (int)t.TotalMinutes)} 分鐘";
}

/// <summary>總結檔已被別人修改（或刪除），不覆寫。</summary>
public sealed class PlantSummaryConflictException() : Exception("總結檔已被別人修改，請重新載入後再操作");

/// <summary>總結檔的讀寫：放在廠別設定檔旁（使用者決定），JSON；存檔前比對讀取時的內容，先寫暫存檔再取代。</summary>
public static class PlantSummaryStore
{
    public const string Extension = ".summary.json";

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>廠別設定檔旁的總結檔：設定檔名（不含 .json）加 .summary.json。</summary>
    public static string PathBeside(string plantConfigPath) =>
        Path.Combine(Path.GetDirectoryName(plantConfigPath) ?? "", Path.GetFileNameWithoutExtension(plantConfigPath) + Extension);

    /// <summary>讀取總結檔；不存在時傳回 (null, null)。格式較新或內容有誤時拋出 FormatException。</summary>
    public static (PlantSummaryFile? File, string? Stamp) Load(string path)
    {
        if (!File.Exists(path)) return (null, null);
        var bytes = File.ReadAllBytes(path);
        try
        {
            using (var doc = JsonDocument.Parse(bytes))
            {
                var version = doc.RootElement.TryGetProperty("formatVersion", out var v) && v.TryGetInt32(out var n) ? n : 0;
                if (version < 1) throw new FormatException("缺少格式版本");
                if (version > PlantSummaryFile.CurrentFormatVersion) throw new FormatException($"格式版本 {version} 較新，請更新程式");
            }
            var file = JsonSerializer.Deserialize<PlantSummaryFile>(bytes, Options) ?? throw new FormatException("內容是空的");
            return (file, StampOf(bytes));
        }
        catch (JsonException ex)
        {
            throw new FormatException("內容有誤：" + ex.Message, ex);
        }
    }

    /// <summary>存檔；檔案內容與讀取時不同（或已被刪除、別人先建立）時拋出 PlantSummaryConflictException，不覆寫。傳回新的內容指紋。</summary>
    public static string Save(string path, PlantSummaryFile file, string? expectedStamp)
    {
        string? current = File.Exists(path) ? StampOf(File.ReadAllBytes(path)) : null;
        if (current != expectedStamp) throw new PlantSummaryConflictException();
        var bytes = new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(file with { FormatVersion = PlantSummaryFile.CurrentFormatVersion }, Options));
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
        return StampOf(bytes);
    }

    static string StampOf(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
