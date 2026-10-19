using CommunityToolkit.Mvvm.ComponentModel;

namespace LogTrace.Workspace.Layout;

/// <summary>站點模式名稱的一列：LOG 中的代號、畫面上顯示的名稱（ADR-0005 第 3 點）。</summary>
public sealed partial class StationModeNameViewModel(string code, string name) : ObservableObject
{
    [ObservableProperty]
    public partial string Code { get; set; } = code;

    [ObservableProperty]
    public partial string Name { get; set; } = name;

    public KeyValuePair<string, string> ToModel() => new(Code.Trim(), Name.Trim());
}
