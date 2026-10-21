using CommunityToolkit.Mvvm.ComponentModel;

namespace LogTrace.Workspace.Layout;

/// <summary>沒有 LOG 的門檻表格的一列（模組、秒數；ADR-0006、#84）。</summary>
public sealed partial class GapThresholdViewModel(string module, string seconds) : ObservableObject
{
    [ObservableProperty]
    public partial string Module { get; set; } = module;

    [ObservableProperty]
    public partial string Seconds { get; set; } = seconds;

    /// <summary>秒數不是整數時存成 -1，由存檔前的檢查擋下（「必須是 0 以上的整數」）。</summary>
    public KeyValuePair<string, int> ToModel() => new(Module.Trim(), int.TryParse(Seconds.Trim(), out var s) ? s : -1);
}
