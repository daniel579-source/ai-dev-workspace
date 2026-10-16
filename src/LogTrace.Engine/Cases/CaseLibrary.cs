using LogTrace.Engine.Plants;

namespace LogTrace.Engine.Cases;

/// <summary>
/// 案件存放位置（ADR-0011 第 3 點）：預設 %LOCALAPPDATA%\LogTrace\Cases，可以修改。
/// </summary>
public sealed class CaseLibrary(string root)
{
    public string Root { get; } = Path.GetFullPath(root);

    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LogTrace", "Cases");

    /// <summary>以案件名稱建立新的案件資料夾；名稱重複時加上序號。</summary>
    public Case Create(string name, PlantConfig plant)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("請輸入案件名稱", nameof(name));
        Directory.CreateDirectory(Root);
        var baseName = SafeFolderName(name.Trim());
        var folder = Path.Combine(Root, baseName);
        for (int i = 2; Directory.Exists(folder); i++) folder = Path.Combine(Root, $"{baseName} ({i})");
        return Case.Create(folder, name.Trim(), plant);
    }

    static string SafeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars).TrimEnd('.', ' ');
    }
}
