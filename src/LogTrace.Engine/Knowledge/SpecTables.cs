using LogTrace.Engine.Parsing;
using LogTrace.Engine.Search;

namespace LogTrace.Engine.Knowledge;

/// <summary>對照表的一列（ADR-0021 第 4 點，候選格式）。File、Line 為來源檔名與行號。</summary>
public sealed record SpecEntry(
    string File, int Line, string Device, string Category, string Code, string Original, string Chinese,
    string Source, string Version, string Status, string Reviewer, string ReviewedAt)
{
    public bool IsReviewed => Status == SpecTables.Reviewed;

    /// <summary>有中文翻譯（原文為日文時由 Copilot 翻譯，顯示時標示「翻譯」）。</summary>
    public bool IsTranslated => Chinese.Length > 0;
}

/// <summary>對照表中沒有使用的列（或整個檔案），或可以使用但需要注意的列（Warning）。</summary>
public sealed record SpecIssue(string File, int Line, string Reason, bool Warning = false);

/// <summary>
/// 電文／代碼對照表（ADR-0021）：機密規格書由已授權的 Copilot 整理、工程師審核後只放本機的知識庫資料夾。
/// 共用的放在 <c>shared\&lt;RM|STV|AGC&gt;\*.csv</c>（各廠共用），各廠補充放在 <c>plants\&lt;廠別&gt;\*.csv</c>。
/// 讀取時同時檢查（工程師只需回報統計），格式有誤的列不使用。只讀取，不存入案件、不記入稽核。
/// </summary>
public sealed class SpecTables
{
    public const string Shared = "shared", Plants = "plants";
    public const string Reviewed = "已審核", Pending = "待確認";
    public static IReadOnlyList<string> Devices { get; } = ["RM", "STV", "AGC"];

    const string DeviceHeader = "設備種類", CategoryHeader = "類別", CodeHeader = "代碼或名稱", OriginalHeader = "意義（原文）",
        ChineseHeader = "意義（中文）", SourceHeader = "出處", VersionHeader = "規格書版本", StatusHeader = "狀態",
        ReviewerHeader = "審核者", ReviewedAtHeader = "審核日期";

    readonly List<(SpecEntry Entry, string Code, string Text)> _normalized;

    SpecTables(List<SpecEntry> entries, List<SpecIssue> issues, List<string> files)
    {
        Entries = entries;
        Issues = issues;
        Files = files;
        _normalized = entries.Select(e => (e, Key(e.Code),
            TextNormalizer.Normalize(string.Join("\n", e.Device, e.Category, e.Code, e.Original, e.Chinese, e.Source)))).ToList();
    }

    public static SpecTables Empty { get; } = new([], [], []);

    /// <summary>可以使用的列，依檔案與行的順序。</summary>
    public IReadOnlyList<SpecEntry> Entries { get; }

    /// <summary>沒有使用的列與原因，以及可以使用但需要注意的列（Warning）。</summary>
    public IReadOnlyList<SpecIssue> Issues { get; }

    /// <summary>讀到的檔案（相對於知識庫資料夾）。</summary>
    public IReadOnlyList<string> Files { get; }

    /// <summary>
    /// 統計（給工程師回報用，不含內容）：例如「對照表 3 個檔、120 筆（已審核 100、待確認 20；翻譯 40）；2 列有誤沒有使用；1 處需要注意」。
    /// </summary>
    public string Summary =>
        Files.Count == 0 ? "沒有對照表"
        : $"對照表 {Files.Count} 個檔、{Entries.Count:N0} 筆（已審核 {Entries.Count(e => e.IsReviewed):N0}、待確認 {Entries.Count(e => !e.IsReviewed):N0}；"
          + $"翻譯 {Entries.Count(e => e.IsTranslated):N0}）"
          + (Issues.Count(i => !i.Warning) is > 0 and var bad ? $"；{bad:N0} 列有誤，沒有使用" : "")
          + (Issues.Count(i => i.Warning) is > 0 and var warn ? $"；{warn:N0} 處需要注意" : "");

    static string Key(string code) => TextNormalizer.Normalize(code.Trim());

    /// <summary>
    /// 代碼完全比對（不分大小寫、全形與半形相同）。devices 為事件對應到的設備種類；null 或空表示不限（對應不到元素時）。
    /// </summary>
    public IReadOnlyList<SpecEntry> Lookup(string code, IReadOnlyCollection<string>? devices = null)
    {
        var key = Key(code);
        if (key.Length == 0) return [];
        return _normalized.Where(n => n.Code == key && (devices is not { Count: > 0 } || devices.Contains(n.Entry.Device)))
            .Select(n => n.Entry).ToList();
    }

    /// <summary>以空白分隔的關鍵字搜尋：部分比對、每個關鍵字都要包含；沒有關鍵字時傳回全部。</summary>
    public IReadOnlyList<SpecEntry> Search(string keywords)
    {
        var terms = keywords.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(TextNormalizer.Normalize).ToList();
        return _normalized.Where(n => terms.All(t => n.Text.Contains(t, StringComparison.Ordinal))).Select(n => n.Entry).ToList();
    }

    /// <summary>
    /// 讀取知識庫資料夾中的對照表：<c>shared\&lt;設備種類&gt;\*.csv</c> 與 <c>plants\&lt;plant&gt;\*.csv</c>。
    /// 資料夾不存在時傳回空的對照表。
    /// </summary>
    public static SpecTables Load(string knowledgeRoot, string? plant)
    {
        var files = new List<(string Name, byte[] Bytes, string? FolderDevice)>();
        foreach (var device in Devices)
        {
            var dir = Path.Combine(knowledgeRoot, Shared, device);
            if (Directory.Exists(dir))
                foreach (var f in Directory.EnumerateFiles(dir, "*.csv").Order(StringComparer.OrdinalIgnoreCase))
                    files.Add((Path.GetRelativePath(knowledgeRoot, f), File.ReadAllBytes(f), device));
        }
        if (!string.IsNullOrWhiteSpace(plant))
        {
            var dir = Path.Combine(knowledgeRoot, Plants, plant);
            if (Directory.Exists(dir))
                foreach (var f in Directory.EnumerateFiles(dir, "*.csv").Order(StringComparer.OrdinalIgnoreCase))
                    files.Add((Path.GetRelativePath(knowledgeRoot, f), File.ReadAllBytes(f), null));
        }
        return Parse(files);
    }

    /// <summary>
    /// 解析多個對照表檔。folderDevice：共用資料夾的設備種類（該檔的「設備種類」可以空白，填了要相同）；各廠補充為 null（必須填）。
    /// 編碼同知識庫：UTF-8（可有 BOM），不是有效的 UTF-8 時改用 Big5。
    /// </summary>
    public static SpecTables Parse(IEnumerable<(string Name, byte[] Bytes, string? FolderDevice)> files)
    {
        var entries = new List<SpecEntry>();
        var issues = new List<SpecIssue>();
        var names = new List<string>();
        var seen = new Dictionary<(string, string, string), SpecEntry>();
        foreach (var (name, bytes, folderDevice) in files)
        {
            names.Add(name);
            var (records, unclosedAt) = KnowledgeBase.ReadRecords(KnowledgeBase.Decode(bytes));
            if (records.Count == 0)
            {
                issues.Add(new(name, 1, "沒有標題列"));
                continue;
            }
            var header = records[0].Fields.Select(HeaderName).ToList();
            int Column(string h) => header.IndexOf(h);
            var missing = new[] { CodeHeader, OriginalHeader, SourceHeader, StatusHeader }
                .Concat(folderDevice is null ? [DeviceHeader] : []).Where(h => Column(h) < 0).ToList();
            if (missing.Count > 0)
            {
                issues.Add(new(name, records[0].Line, $"標題列缺少「{string.Join("」「", missing)}」欄"));
                continue;
            }
            int device = Column(DeviceHeader), category = Column(CategoryHeader), code = Column(CodeHeader), original = Column(OriginalHeader),
                chinese = Column(ChineseHeader), source = Column(SourceHeader), version = Column(VersionHeader), status = Column(StatusHeader),
                reviewer = Column(ReviewerHeader), reviewedAt = Column(ReviewedAtHeader);
            foreach (var (line, fields) in records.Skip(1))
            {
                if (fields.All(string.IsNullOrWhiteSpace)) continue;
                string Get(int i) => i >= 0 && i < fields.Count ? fields[i].Trim() : "";
                var dev = Get(device).ToUpperInvariant();
                if (dev.Length == 0 && folderDevice is not null) dev = folderDevice;
                var e = new SpecEntry(name, line, dev, Get(category), Get(code), Get(original), Get(chinese), Get(source), Get(version),
                    Get(status), Get(reviewer), Get(reviewedAt));
                string? problem =
                    e.Code.Length == 0 ? "沒有填代碼或名稱"
                    : e.Original.Length == 0 ? "沒有填意義（原文）"
                    : e.Source.Length == 0 ? "沒有填出處"
                    : !Devices.Contains(e.Device) ? $"設備種類「{e.Device}」不是 RM、STV 或 AGC"
                    : folderDevice is not null && e.Device != folderDevice ? $"設備種類「{e.Device}」和資料夾「{folderDevice}」不同"
                    : e.Status is not (Reviewed or Pending) ? $"狀態「{e.Status}」必須是「{Reviewed}」或「{Pending}」"
                    : null;
                if (problem is not null)
                {
                    issues.Add(new(name, line, problem));
                    continue;
                }
                var key = (e.Device, TextNormalizer.Normalize(e.Category), Key(e.Code));
                if (seen.TryGetValue(key, out var first))
                {
                    if (TextNormalizer.Normalize(first.Original) == TextNormalizer.Normalize(e.Original))
                    {
                        issues.Add(new(name, line, $"和 {first.File} 第 {first.Line} 行重複，沒有使用"));
                        continue;
                    }
                    issues.Add(new(name, line, $"和 {first.File} 第 {first.Line} 行同一代碼但意義不同（兩列都會顯示，請確認）", Warning: true));
                }
                else seen[key] = e;
                if (e.Version.Length == 0) issues.Add(new(name, line, "沒有填規格書版本", Warning: true));
                entries.Add(e);
            }
            if (unclosedAt is { } at) issues.Add(new(name, at, "雙引號沒有結束"));
        }
        return new(entries, issues, names);
    }

    /// <summary>標題的寫法：去掉前後空白，半形括號視為全形（「意義(原文)」＝「意義（原文）」）。</summary>
    static string HeaderName(string h) => h.Trim().Replace('(', '（').Replace(')', '）');
}
