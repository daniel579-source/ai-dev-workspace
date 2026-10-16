using System.Text.RegularExpressions;

namespace LogTrace.Engine.Import;

/// <summary>匯入來源中的一個 LOG 檔。</summary>
public sealed record SourceFile(string FullPath, string Module, string DayFolder, string Logger, string RelativePath, int StreamOrder);

/// <summary>
/// 辨識「日期 → `&lt;模組&gt;-&lt;日期&gt;`」結構（docs/log-format-notes.md 2.1、ADR-0002）。
/// 來源可以是含日期資料夾的上層資料夾，或日期資料夾本身。輪替檔在 archives\ 下，封存在前、現行檔在後。
/// </summary>
public static partial class LogFolderScanner
{
    [GeneratedRegex(@"^\d{8}$")]
    private static partial Regex DayFolderName();

    [GeneratedRegex(@"^(?<module>.+)-\d{4}-\d{2}-\d{2}$")]
    private static partial Regex ModuleFolderName();

    [GeneratedRegex(@"^(?<logger>.+)_(?<ts>\d{4}-\d{2}-\d{2} \d{2}_\d{2}_\d{2}\.\d{4})$")]
    private static partial Regex ArchiveFileName();

    public static IReadOnlyList<SourceFile> Scan(string sourceFolder)
    {
        var root = Path.GetFullPath(sourceFolder);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"找不到資料夾：{root}");
        var dayDirs = DayFolderName().IsMatch(Path.GetFileName(root))
            ? [root]
            : Directory.GetDirectories(root).Where(d => DayFolderName().IsMatch(Path.GetFileName(d))).Order(StringComparer.Ordinal).ToArray();

        var result = new List<SourceFile>();
        foreach (var dayDir in dayDirs)
        {
            var day = Path.GetFileName(dayDir);
            foreach (var moduleDir in Directory.GetDirectories(dayDir).Order(StringComparer.Ordinal))
            {
                var m = ModuleFolderName().Match(Path.GetFileName(moduleDir));
                if (!m.Success) continue;
                var module = m.Groups["module"].Value;

                var streams = new Dictionary<string, List<(string SortKey, string Path)>>(StringComparer.Ordinal);
                void Add(string path, bool archived)
                {
                    var name = Path.GetFileNameWithoutExtension(path);
                    string logger = name, key = "~current";
                    if (archived && ArchiveFileName().Match(name) is { Success: true } am)
                    {
                        logger = am.Groups["logger"].Value;
                        key = am.Groups["ts"].Value;
                    }
                    if (!streams.TryGetValue(logger, out var list)) streams[logger] = list = [];
                    list.Add((key, path));
                }
                foreach (var f in Directory.GetFiles(moduleDir, "*.txt")) Add(f, archived: false);
                var archives = Path.Combine(moduleDir, "archives");
                if (Directory.Exists(archives))
                    foreach (var f in Directory.GetFiles(archives, "*.txt")) Add(f, archived: true);

                foreach (var (logger, files) in streams.OrderBy(s => s.Key, StringComparer.Ordinal))
                {
                    int order = 0;
                    foreach (var (_, path) in files.OrderBy(f => f.SortKey, StringComparer.Ordinal))
                    {
                        var rel = Path.GetRelativePath(Path.GetDirectoryName(dayDir)!, path).Replace('\\', '/');
                        result.Add(new SourceFile(path, module, day, logger, rel, order++));
                    }
                }
            }
        }
        return result;
    }
}
