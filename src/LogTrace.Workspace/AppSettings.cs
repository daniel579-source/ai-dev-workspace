using System.Text.Json;

namespace LogTrace.Workspace;

/// <summary>程式設定（依使用者保存）：案件存放位置與最近使用的案件。</summary>
public sealed record AppSettings
{
    public const int MaxRecentCases = 10;

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
