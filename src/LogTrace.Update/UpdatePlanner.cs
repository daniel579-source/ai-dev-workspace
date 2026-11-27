namespace LogTrace.Update;

/// <summary>對一個升級包要做的事。</summary>
public enum PlanAction
{
    /// <summary>解開 WebView2 Runtime。</summary>
    InstallRuntime,

    /// <summary>解開程式版本；SwitchTo 為 true 時改成預設啟動的版本。</summary>
    InstallApp,

    /// <summary>需要的 Runtime 還沒有：升級包留在 updates\，Runtime 到了再套用（ADR-0030 第 3 點第 7 步）。</summary>
    WaitForRuntime,

    /// <summary>無法套用：移到 rejected\ 並寫原因。</summary>
    Reject,
}

public sealed record PlanItem(PlanAction Action, PackageInspection Package, string? Reason = null, bool SwitchTo = false)
{
    public UpdateManifest Manifest => Package.Manifest!;
}

/// <summary>
/// 依 updates\ 的升級包與已安裝的版本，決定每個升級包要做的事（ADR-0030 第 3 點）：
/// <list type="bullet">
/// <item>Runtime 先解開；已經有的 Runtime 不再解開。</item>
/// <item>比已安裝最新版新的程式包：需要的 Runtime 有了（已安裝或同一批）才解開，其中最新的一版改成預設啟動的版本；Runtime 沒有時等待。</item>
/// <item>版本等於或低於已安裝最新版的程式包：版本資料夾還不存在時照樣解開，但不切換（第 8 步）；已經存在時不套用。</item>
/// <item>同一批中重複的版本只用第一個。</item>
/// </list>
/// </summary>
public static class UpdatePlanner
{
    public static IReadOnlyList<PlanItem> Plan(ProgramFolder folder, IReadOnlyList<PackageInspection> packages)
    {
        var plan = new List<PlanItem>();
        var runtimes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in packages.Where(p => p.Manifest is null))
            plan.Add(new(PlanAction.Reject, p, p.Problem));

        foreach (var p in packages.Where(p => p.Manifest is { IsRuntime: true }))
        {
            var v = p.Manifest!.Version;
            if (folder.HasRuntime(v)) plan.Add(new(PlanAction.Reject, p, $"WebView2 Runtime {v} 已經安裝"));
            else if (!runtimes.Add(v)) plan.Add(new(PlanAction.Reject, p, $"同一批中有重複的 WebView2 Runtime {v}"));
            else plan.Add(new(PlanAction.InstallRuntime, p));
        }

        var installed = folder.InstalledApps();
        var newest = installed.Count == 0 ? null : installed[^1];
        var apps = new List<PackageInspection>();
        var seen = new HashSet<Version>();
        foreach (var p in packages.Where(p => p.Manifest is { IsApp: true }).OrderBy(p => p.Manifest!.AppVersion))
        {
            var v = p.Manifest!.AppVersion;
            if (folder.HasApp(v)) plan.Add(new(PlanAction.Reject, p, $"程式 {v} 已經安裝"));
            else if (!seen.Add(v)) plan.Add(new(PlanAction.Reject, p, $"同一批中有重複的程式 {v}"));
            else if (!folder.HasRuntime(p.Manifest.RuntimeVersion) && !runtimes.Contains(p.Manifest.RuntimeVersion))
                plan.Add(new(PlanAction.WaitForRuntime, p, $"程式 {v} 需要 WebView2 Runtime {p.Manifest.RuntimeVersion}，請一併放入 Runtime 包"));
            else apps.Add(p);
        }
        var target = apps.Where(p => newest is null || p.Manifest!.AppVersion > newest).Select(p => p.Manifest!.AppVersion).DefaultIfEmpty().Max();
        foreach (var p in apps)
            plan.Add(new(PlanAction.InstallApp, p, null, SwitchTo: target is not null && p.Manifest!.AppVersion == target));
        return plan;
    }
}
