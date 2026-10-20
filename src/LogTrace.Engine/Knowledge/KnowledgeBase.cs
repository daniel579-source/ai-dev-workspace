using System.Text;
using LogTrace.Engine.Parsing;
using LogTrace.Engine.Plants;
using LogTrace.Engine.Search;

namespace LogTrace.Engine.Knowledge;

/// <summary>知識庫的一列：代碼、說明（必填），處置、出處（可以空白）；Line 為該列在檔案中開始的行號。</summary>
public sealed record KnowledgeEntry(int Line, string Code, string Description, string Action, string Source);

/// <summary>沒有使用的列（或整個檔案）：行號與原因。</summary>
public sealed record KnowledgeIssue(int Line, string Reason);

/// <summary>
/// 知識庫第一版（ADR-0013 第 2 點）：每個廠別一個 CSV 檔（<c>&lt;廠別設定檔名&gt;.kb.csv</c>），放在廠別設定旁邊，不進 repo。
/// 第一列為標題，欄位 代碼、說明、處置、出處（順序不拘，多出的欄位不使用）。UTF-8（可有 BOM），不是有效的 UTF-8 時改用 Big5。
/// 只讀取，不存入案件快照，也不記入稽核。
/// </summary>
public sealed class KnowledgeBase
{
    public const string Extension = ".kb.csv";

    const string CodeHeader = "代碼", DescriptionHeader = "說明", ActionHeader = "處置", SourceHeader = "出處";

    readonly List<(KnowledgeEntry Entry, string Code, string Text)> _normalized;

    KnowledgeBase(List<KnowledgeEntry> entries, List<KnowledgeIssue> issues)
    {
        Entries = entries;
        Issues = issues;
        _normalized = entries.Select(e => (e, Key(e.Code),
            TextNormalizer.Normalize(string.Join("\n", e.Code, e.Description, e.Action, e.Source)))).ToList();
    }

    /// <summary>可以使用的列，依檔案順序。</summary>
    public IReadOnlyList<KnowledgeEntry> Entries { get; }

    /// <summary>沒有使用的列與原因。</summary>
    public IReadOnlyList<KnowledgeIssue> Issues { get; }

    /// <summary>代碼完全比對：不分大小寫、全形與半形相同（ADR-0010 的正規化），前後空白不計。同一代碼的多列依檔案順序。</summary>
    public IReadOnlyList<KnowledgeEntry> Lookup(string code)
    {
        var key = Key(code);
        return key.Length == 0 ? [] : _normalized.Where(n => n.Code == key).Select(n => n.Entry).ToList();
    }

    /// <summary>以空白分隔的關鍵字搜尋四個欄位：部分比對、每個關鍵字都要包含；沒有關鍵字時傳回全部。</summary>
    public IReadOnlyList<KnowledgeEntry> Search(string keywords)
    {
        var terms = keywords.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(TextNormalizer.Normalize).ToList();
        return _normalized.Where(n => terms.All(t => n.Text.Contains(t, StringComparison.Ordinal))).Select(n => n.Entry).ToList();
    }

    static string Key(string code) => TextNormalizer.Normalize(code.Trim());

    /// <summary>讀取知識庫檔；檔案不存在時傳回 null。</summary>
    public static KnowledgeBase? Load(string path) => File.Exists(path) ? Parse(File.ReadAllBytes(path)) : null;

    /// <summary>
    /// 案件所用廠別的知識庫檔路徑：先看快照的來源路徑，再依廠別名稱找存放位置下的設定檔（與找新版設定的順序相同）。
    /// 找不到設定檔時傳回 null；知識庫檔本身不一定存在。
    /// </summary>
    public static string? PathFor(string plantName, string? snapshotSource, PlantConfigLibrary library)
    {
        var candidates = new List<string>();
        if (snapshotSource is not null && File.Exists(snapshotSource)) candidates.Add(snapshotSource);
        candidates.AddRange(library.List().Where(p => !candidates.Contains(p, StringComparer.OrdinalIgnoreCase)));
        foreach (var path in candidates)
        {
            try
            {
                if (string.Equals(PlantConfigFile.Load(path).Plant, plantName, StringComparison.Ordinal)) return PathBeside(path);
            }
            catch (Exception ex) when (ex is PlantConfigFormatException or IOException or UnauthorizedAccessException)
            {
                // 讀不了的設定檔略過
            }
        }
        return null;
    }

    /// <summary>廠別設定檔旁邊的知識庫檔：同一資料夾，檔名為設定檔名（不含 .json）加 .kb.csv。</summary>
    public static string PathBeside(string plantConfigPath) =>
        Path.Combine(Path.GetDirectoryName(plantConfigPath) ?? "", Path.GetFileNameWithoutExtension(plantConfigPath) + Extension);

    public static KnowledgeBase Parse(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> bom = [0xEF, 0xBB, 0xBF];
        if (bytes.StartsWith(bom)) bytes = bytes[bom.Length..];
        var text = LogEncoding.Get(LogEncoding.Detect(bytes)).GetString(bytes);

        var entries = new List<KnowledgeEntry>();
        var issues = new List<KnowledgeIssue>();
        var (records, unclosedAt) = ReadRecords(text);
        if (records.Count == 0)
        {
            issues.Add(new(1, "沒有標題列"));
            return new(entries, issues);
        }

        var header = records[0].Fields.Select(f => f.Trim()).ToList();
        int Column(string name) => header.IndexOf(name);
        int code = Column(CodeHeader), description = Column(DescriptionHeader), action = Column(ActionHeader), source = Column(SourceHeader);
        if (code < 0 || description < 0)
        {
            issues.Add(new(records[0].Line, $"標題列缺少「{(code < 0 ? CodeHeader : DescriptionHeader)}」欄"));
            return new(entries, issues);
        }

        foreach (var (line, fields) in records.Skip(1))
        {
            if (fields.All(string.IsNullOrWhiteSpace)) continue; // 空白列
            string Get(int i) => i >= 0 && i < fields.Count ? fields[i].Trim() : "";
            var entry = new KnowledgeEntry(line, Get(code), Get(description), Get(action), Get(source));
            if (entry.Code.Length == 0) issues.Add(new(line, "沒有填代碼"));
            else if (entry.Description.Length == 0) issues.Add(new(line, "沒有填說明"));
            else entries.Add(entry);
        }
        if (unclosedAt is { } at) issues.Add(new(at, "雙引號沒有結束"));
        return new(entries, issues);
    }

    /// <summary>
    /// 依 RFC 4180 切出每一列：雙引號內可以有逗號、換行，兩個雙引號代表一個雙引號。換行接受 CRLF 與 LF。
    /// 雙引號沒有結束時，那一列不傳回，另外傳回它開始的行號。
    /// </summary>
    static (List<(int Line, List<string> Fields)> Records, int? UnclosedAt) ReadRecords(string text)
    {
        var records = new List<(int, List<string>)>();
        var fields = new List<string>();
        var field = new StringBuilder();
        int line = 1, recordLine = 1;
        bool quoted = false, any = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else quoted = false;
                }
                else
                {
                    if (c == '\n') line++;
                    if (c != '\r') field.Append(c);
                }
                continue;
            }
            switch (c)
            {
                case '"':
                    quoted = true;
                    any = true;
                    break;
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    any = true;
                    break;
                case '\r':
                    break;
                case '\n':
                    fields.Add(field.ToString());
                    records.Add((recordLine, fields));
                    fields = [];
                    field.Clear();
                    any = false;
                    recordLine = ++line;
                    break;
                default:
                    field.Append(c);
                    any = true;
                    break;
            }
        }
        if (quoted) return (records, recordLine);
        if (any || field.Length > 0)
        {
            fields.Add(field.ToString());
            records.Add((recordLine, fields));
        }
        return (records, null);
    }
}
