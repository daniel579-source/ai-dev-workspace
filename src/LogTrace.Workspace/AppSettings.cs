using System.Text.Json;

namespace LogTrace.Workspace;

/// <summary>一次搜尋的條件（最近的搜尋，ADR-0010 第 4 點）。</summary>
public sealed record SavedSearch
{
    public string AllOf { get; init; } = "";
    public string AnyOf { get; init; } = "";
    public string NoneOf { get; init; } = "";
    public bool CaseSensitive { get; init; }
    public bool Regex { get; init; }
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public IReadOnlyList<string> Modules { get; init; } = [];
    public IReadOnlyList<string> Loggers { get; init; } = [];
    public IReadOnlyList<string> Levels { get; init; } = [];
    public int Flags { get; init; }
    public FieldFilterKind FieldKind { get; init; }
    public string FieldValue { get; init; } = "";

    /// <summary>清單中顯示的摘要。</summary>
    public string Label
    {
        get
        {
            var parts = new List<string>();
            if (AllOf.Trim().Length > 0) parts.Add($"全部：{AllOf.Trim()}");
            if (AnyOf.Trim().Length > 0) parts.Add($"任一：{AnyOf.Trim()}");
            if (NoneOf.Trim().Length > 0) parts.Add($"不含：{NoneOf.Trim()}");
            if (FieldKind != FieldFilterKind.None && FieldValue.Length > 0) parts.Add($"{FieldFilterOption.Of(FieldKind).Label}：{FieldValue}");
            if (Modules.Count + Loggers.Count + Levels.Count > 0 || Flags != 0 || From.Length + To.Length > 0) parts.Add("＋篩選");
            if (Regex) parts.Add("正規表示式");
            if (CaseSensitive) parts.Add("區分大小寫");
            return parts.Count == 0 ? "（全部事件）" : string.Join("　", parts);
        }
    }
}

/// <summary>程式設定（依使用者保存）：案件存放位置、最近使用的案件、最近的搜尋。</summary>
public sealed record AppSettings
{
    public const int MaxRecentCases = 10;
    public const int MaxRecentSearches = 20;

    /// <summary>最近的搜尋，最近的在前（ADR-0010 第 4 點：記住最近 20 次）。</summary>
    public IReadOnlyList<SavedSearch> RecentSearches { get; init; } = [];

    public AppSettings WithSearch(SavedSearch search) => this with
    {
        RecentSearches = [search, .. RecentSearches.Where(s => s != search && !SameContent(s, search)).Take(MaxRecentSearches - 1)],
    };

    static bool SameContent(SavedSearch a, SavedSearch b) =>
        a with { Modules = [], Loggers = [], Levels = [] } == b with { Modules = [], Loggers = [], Levels = [] }
        && a.Modules.SequenceEqual(b.Modules) && a.Loggers.SequenceEqual(b.Loggers) && a.Levels.SequenceEqual(b.Levels);

    /// <summary>案件存放位置；null 表示使用預設位置（ADR-0011 第 3 點）。</summary>
    public string? CasesRoot { get; init; }

    /// <summary>最近使用的案件資料夾，最近的在前。</summary>
    public IReadOnlyList<string> RecentCases { get; init; } = [];

    public AppSettings WithRecent(string folder) => this with
    {
        RecentCases = [folder, .. RecentCases.Where(f => !string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)).Take(MaxRecentCases - 1)],
    };

    public AppSettings WithoutRecent(string folder) => this with
    {
        RecentCases = RecentCases.Where(f => !string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)).ToList(),
    };
}

/// <summary>設定檔（JSON）。讀取失敗時使用預設值，不阻擋程式啟動。</summary>
public sealed class SettingsStore(string path)
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string Path { get; } = path;

    public AppSettings Load()
    {
        try
        {
            return File.Exists(Path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonSerializer.Serialize(settings, Options));
    }
}
