namespace LogTrace.Engine.Cases;

/// <summary>
/// 判斷案件存放位置是否在 OneDrive 同步資料夾內（ADR-0011 第 3 點）。案件開啟時資料庫一直在變動，
/// 同步程式可能複製到不完整的檔案，或把真實 LOG 同步到雲端（ADR-0009）。
/// </summary>
public static class OneDriveDetector
{
    /// <summary>
    /// 本機的 OneDrive 同步根目錄：環境變數 OneDrive／OneDriveCommercial／OneDriveConsumer，
    /// 以及登錄 HKCU\Software\Microsoft\OneDrive\Accounts\*\UserFolder（企業帳號）。
    /// </summary>
    public static IReadOnlyList<string> LocalSyncRoots()
    {
        var roots = new List<string>();
        foreach (var name in new[] { "OneDrive", "OneDriveCommercial", "OneDriveConsumer" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } v) roots.Add(v);
        if (OperatingSystem.IsWindows())
        {
            using var accounts = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
            foreach (var name in accounts?.GetSubKeyNames() ?? [])
            {
                using var key = accounts!.OpenSubKey(name);
                if (key?.GetValue("UserFolder") is string { Length: > 0 } folder) roots.Add(folder);
            }
        }
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// 「已知資料夾」（桌面、文件、圖片）目前的實際位置。企業將已知資料夾移到 OneDrive（Known Folder Move）時，
    /// 這些位置會在 OneDrive 底下。
    /// </summary>
    public static IReadOnlyList<string> LocalKnownFolders() =>
        new[] { Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.MyPictures }
            .Select(Environment.GetFolderPath).Where(p => p.Length > 0).ToList();

    public static bool IsInOneDrive(string path) => IsInOneDrive(path, LocalSyncRoots(), LocalKnownFolders());

    /// <summary>
    /// path 在任何一個同步根目錄下；或在一個已知資料夾下，而該資料夾本身在同步根目錄下或路徑中有「OneDrive」資料夾；
    /// 或 path 本身經過名稱以「OneDrive」開頭的資料夾（例如「OneDrive - 公司名稱」）。
    /// </summary>
    public static bool IsInOneDrive(string path, IEnumerable<string> syncRoots, IEnumerable<string> knownFolders)
    {
        var full = Normalize(path);
        var roots = syncRoots.Select(Normalize).ToList();
        if (roots.Any(r => IsUnder(full, r)) || HasOneDriveSegment(full)) return true;
        return knownFolders.Select(Normalize)
            .Any(k => IsUnder(full, k) && (roots.Any(r => IsUnder(k, r)) || HasOneDriveSegment(k)));
    }

    static string Normalize(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    static bool IsUnder(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    static bool HasOneDriveSegment(string path) =>
        path.Split(Path.DirectorySeparatorChar).Any(s => s.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase));
}
