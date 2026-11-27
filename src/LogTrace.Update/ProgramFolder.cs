using System.Text.Json;

namespace LogTrace.Update;

/// <summary>
/// 程式根資料夾（ADR-0030 第 1 點），以啟動程式所在位置為準：
/// <code>
/// LogTrace.exe                啟動程式
/// launcher.json               預設啟動的版本
/// updates\  updates\rejected\ 升級包、無法套用的升級包
/// app-&lt;版本&gt;\               每個版本一個資料夾，全部保留
/// runtimes\webview2-&lt;版本&gt;\  WebView2 Runtime，多個版本共用
/// </code>
/// 版本資料夾與 Runtime 資料夾解開完成後才會有 installed.json；沒有的視為不完整，不使用。
/// </summary>
public sealed class ProgramFolder(string root)
{
    public const string InstalledFileName = "installed.json";
    public const string StateFileName = "launcher.json";
    public const string AppExeName = "LogTrace.exe";

    public string Root { get; } = Path.GetFullPath(root);

    public string UpdatesFolder => Path.Combine(Root, "updates");

    public string RejectedFolder => Path.Combine(UpdatesFolder, "rejected");

    public string StateFile => Path.Combine(Root, StateFileName);

    public string AppFolder(Version version) => Path.Combine(Root, $"app-{version}");

    public string RuntimeFolder(string version) => Path.Combine(Root, "runtimes", $"webview2-{version}");

    public string AppExe(Version version) => Path.Combine(AppFolder(version), AppExeName);

    /// <summary>解開完成的版本，由舊到新。</summary>
    public IReadOnlyList<Version> InstalledApps()
    {
        if (!Directory.Exists(Root)) return [];
        var list = new List<Version>();
        foreach (var dir in Directory.GetDirectories(Root, "app-*"))
            if (Version.TryParse(Path.GetFileName(dir)[4..], out var v) && File.Exists(Path.Combine(dir, InstalledFileName))
                && File.Exists(Path.Combine(dir, AppExeName)))
                list.Add(v);
        list.Sort();
        return list;
    }

    public bool HasApp(Version version) => File.Exists(Path.Combine(AppFolder(version), InstalledFileName));

    public bool HasRuntime(string version) => File.Exists(Path.Combine(RuntimeFolder(version), InstalledFileName));

    /// <summary>已解開版本的 update.json（安裝時保存為 installed.json）；讀不了時為 null。</summary>
    public UpdateManifest? InstalledManifest(Version version)
    {
        try
        {
            return UpdateManifest.Parse(File.ReadAllText(Path.Combine(AppFolder(version), InstalledFileName)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// 預設啟動的版本：launcher.json 記錄的版本；檔案不見、讀不了或那個版本不完整時，改用最新且完整的版本。沒有任何版本時為 null。
    /// </summary>
    public Version? CurrentApp()
    {
        try
        {
            if (File.Exists(StateFile)
                && JsonSerializer.Deserialize(File.ReadAllText(StateFile), UpdateJson.Default.LauncherState) is { } state
                && Version.TryParse(state.Current, out var v) && HasApp(v) && File.Exists(AppExe(v)))
                return v;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 改用最新的版本
        }
        var installed = InstalledApps();
        return installed.Count == 0 ? null : installed[^1];
    }

    public void SetCurrent(Version version)
    {
        var temp = StateFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new LauncherState(version.ToString()), UpdateJson.Default.LauncherState));
        File.Move(temp, StateFile, overwrite: true);
    }

    /// <summary>updates\ 下的升級包（只看 .zip，不含 rejected\），依檔名排序。</summary>
    public IReadOnlyList<string> PendingPackages() =>
        Directory.Exists(UpdatesFolder)
            ? Directory.GetFiles(UpdatesFolder, "*.zip").Order(StringComparer.OrdinalIgnoreCase).ToList()
            : [];
}
