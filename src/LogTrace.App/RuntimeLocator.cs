using System.IO;
using System.Reflection;

namespace LogTrace.App;

/// <summary>
/// 找 WebView2 固定版本 Runtime（ADR-0017 第 2 點、ADR-0030 第 1 點，2026-11-27 使用者選 5a）：
/// 先找程式資料夾內的 WebView2Runtime\（開發與測試時），找不到再找程式根資料夾共用的 ..\runtimes\webview2-&lt;版本&gt;\。
/// 兩處都沒有時傳回共用資料夾的路徑（錯誤訊息顯示這個位置）。
/// </summary>
public static class RuntimeLocator
{
    /// <summary>建置時寫入的 Runtime 版本（LogTrace.App.csproj 的 WebView2RuntimeVersion）。</summary>
    public static string RequiredVersion { get; } =
        typeof(RuntimeLocator).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "WebView2RuntimeVersion")?.Value ?? "";

    public static string Find(string appFolder, string version)
    {
        var own = Path.Combine(appFolder, "WebView2Runtime");
        if (File.Exists(Path.Combine(own, "msedgewebview2.exe"))) return own;
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(appFolder)) ?? appFolder;
        return Path.Combine(parent, "runtimes", $"webview2-{version}");
    }
}
