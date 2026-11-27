using System.Text;

namespace LogTrace.Update;

/// <summary>
/// 啟動程式的升級流程（ADR-0030 第 3 點），不含畫面：畫面由呼叫端提供「詢問」「告知」與「顯示進度」。
/// </summary>
public static class UpdateRunner
{
    /// <summary>確認畫面中更新說明的長度上限；超過時截斷並註明完整內容在 CHANGELOG.md（2026-11-27 使用者選 1a）。</summary>
    public const int NotesLimit = 1200;

    /// <summary>掃描 updates\ 並規劃。</summary>
    public static IReadOnlyList<PlanItem> Scan(ProgramFolder folder) =>
        UpdatePlanner.Plan(folder, folder.PendingPackages().Select(PackageInspector.Inspect).ToList());

    /// <summary>
    /// 把不能套用的升級包移到 rejected\，傳回要告知使用者的說明（含等待 Runtime 的程式包）；沒有時為 null。
    /// </summary>
    public static string? RejectAndDescribe(ProgramFolder folder, IReadOnlyList<PlanItem> plan)
    {
        var lines = new List<string>();
        foreach (var r in plan.Where(p => p.Action == PlanAction.Reject))
        {
            var moved = UpdateInstaller.Reject(folder, r.Package.Path, r.Reason ?? "無法套用");
            lines.Add($"・{r.Package.FileName}：{r.Reason}{(moved is null ? "（無法移到 rejected\\，請自行移除）" : "")}");
        }
        if (lines.Count > 0) lines.Insert(0, "下列升級包無法套用，已移到 updates\\rejected\\（旁邊有原因說明）：");
        var waits = plan.Where(p => p.Action == PlanAction.WaitForRuntime).ToList();
        if (waits.Count > 0)
        {
            if (lines.Count > 0) lines.Add("");
            lines.Add("下列程式包先不套用，留在 updates\\：");
            lines.AddRange(waits.Select(w => $"・{w.Package.FileName}：{w.Reason}"));
        }
        return lines.Count == 0 ? null : string.Join("\r\n", lines);
    }

    /// <summary>要套用的項目（Runtime 在前）。</summary>
    public static IReadOnlyList<PlanItem> Installable(IReadOnlyList<PlanItem> plan) =>
        plan.Where(p => p.Action == PlanAction.InstallRuntime)
            .Concat(plan.Where(p => p.Action == PlanAction.InstallApp).OrderBy(p => p.Manifest.AppVersion))
            .ToList();

    /// <summary>確認畫面的內容：目前版本、新版本、要解開的 Runtime、新版本那一版的更新說明（第 4 步）。</summary>
    public static string ConfirmText(IReadOnlyList<PlanItem> installable, Version? current)
    {
        var sb = new StringBuilder("在 updates\\ 找到升級包：\r\n");
        var target = installable.FirstOrDefault(p => p.SwitchTo);
        if (target is not null) sb.Append($"・程式 {current?.ToString() ?? "（沒有）"} → {target.Manifest.Version}\r\n");
        foreach (var p in installable.Where(p => p.Action == PlanAction.InstallApp && !p.SwitchTo))
            sb.Append($"・程式 {p.Manifest.Version}（只解開，不改成預設啟動的版本）\r\n");
        foreach (var p in installable.Where(p => p.Action == PlanAction.InstallRuntime))
            sb.Append($"・WebView2 Runtime {p.Manifest.Version}（約 {p.Package.UncompressedBytes / (1024 * 1024):N0} MB）\r\n");
        sb.Append("\r\n解開可能需要一些時間，期間會顯示進度。\r\n");
        if (target is not null && !string.IsNullOrWhiteSpace(target.Manifest.Notes))
        {
            var notes = target.Manifest.Notes.Trim().ReplaceLineEndings("\r\n");
            if (notes.Length > NotesLimit) notes = notes[..NotesLimit] + "…\r\n（說明太長，完整內容見程式資料夾內的 CHANGELOG.md）";
            sb.Append($"\r\n{target.Manifest.Version} 更新說明：\r\n{notes}\r\n");
        }
        sb.Append("\r\n要現在套用嗎？\r\n「是」＝套用；「否」＝稍後（下次啟動時再詢問）。");
        return sb.ToString();
    }

    /// <summary>
    /// 依序解開，成功的刪除升級包、失敗的移到 rejected\；最後切換預設啟動的版本（只有那一版解開成功時）。
    /// status 回報目前在做什麼，progress 回報整體進度 0～1。傳回失敗的說明（全部成功時為空）。
    /// </summary>
    public static IReadOnlyList<string> Apply(ProgramFolder folder, IReadOnlyList<PlanItem> installable,
        IProgress<string>? status = null, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var errors = new List<string>();
        var total = Math.Max(1, installable.Sum(p => p.Package.UncompressedBytes));
        long before = 0;
        var failedRuntimes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in installable)
        {
            var m = item.Manifest;
            status?.Report($"正在解開 {m.Describe()}…");
            var size = item.Package.UncompressedBytes;
            var start = before;
            var step = new Progress(f => progress?.Report((start + f * size) / total));
            try
            {
                if (m.IsApp && failedRuntimes.Contains(m.RuntimeVersion))
                    throw new UpdateException($"需要的 WebView2 Runtime {m.RuntimeVersion} 解開失敗");
                UpdateInstaller.Install(folder, item, step, ct);
                UpdateInstaller.RemovePackage(item.Package.Path);
                if (item.SwitchTo) folder.SetCurrent(m.AppVersion);
            }
            catch (UpdateException ex)
            {
                if (m.IsRuntime) failedRuntimes.Add(m.Version);
                UpdateInstaller.Reject(folder, item.Package.Path, ex.Message);
                errors.Add($"・{item.Package.FileName}：{ex.Message}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"・{item.Package.FileName}：{ex.Message}");
            }
            before += size;
        }
        progress?.Report(1);
        return errors;
    }

    /// <summary>同步回報的 IProgress（System.Progress 會切到別的執行緒）。</summary>
    sealed class Progress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
