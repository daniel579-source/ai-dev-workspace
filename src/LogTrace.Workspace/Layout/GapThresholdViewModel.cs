using CommunityToolkit.Mvvm.ComponentModel;

namespace LogTrace.Workspace.Layout;

/// <summary>沒有 LOG 的門檻表格的一列（模組、秒數；ADR-0006、#84）。</summary>
public sealed partial class GapThresholdViewModel(string module, string seconds) : ObservableObject
{
    [ObservableProperty]
    public partial string Module { get; set; } = module;

    [ObservableProperty]
    public partial string Seconds { get; set; } = seconds;

    /// <summary>
    /// 模組的下拉選項（#86）：模組順序，加上這一列目前的模組（不在模組順序中時保留顯示，存檔時擋下），
    /// 免得下拉選單把舊名稱清掉。
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> ModuleChoices { get; private set; } = [];

    // 選項換掉時下拉選單可能寫回 null；這一列的模組一定在選項中，所以只忽略 null。
    partial void OnModuleChanging(string oldValue, string newValue) => _restore = newValue is null ? oldValue : null;
    partial void OnModuleChanged(string value)
    {
        if (_restore is { } old) { _restore = null; Module = old; }
    }
    string? _restore;

    internal void SetModuleChoices(IReadOnlyList<string> moduleOrder)
    {
        var current = Module?.Trim() ?? "";
        ModuleChoices = current.Length == 0 || moduleOrder.Contains(current, StringComparer.OrdinalIgnoreCase)
            ? moduleOrder : [.. moduleOrder, current];
    }

    /// <summary>秒數不是整數時存成 -1，由存檔前的檢查擋下（「必須是 0 以上的整數」）。</summary>
    public KeyValuePair<string, int> ToModel() => new((Module ?? "").Trim(), int.TryParse(Seconds.Trim(), out var s) ? s : -1);
}
