using CommunityToolkit.Mvvm.ComponentModel;
using LogTrace.Engine.Parsing;
using LogTrace.Engine.Plants;

namespace LogTrace.Workspace.Layout;

/// <summary>編碼設定的一列：模組（空白表示所有模組）、Logger、編碼（docs/log-format-notes.md O-002）。</summary>
public sealed partial class EncodingRuleViewModel(string module, string logger, string encoding) : ObservableObject
{
    public static IReadOnlyList<string> Choices { get; } = [LogEncoding.Utf8Name, LogEncoding.Big5Name];

    [ObservableProperty]
    public partial string Module { get; set; } = module;

    [ObservableProperty]
    public partial string Logger { get; set; } = logger;

    [ObservableProperty]
    public partial string Encoding { get; set; } = encoding;

    public static EncodingRuleViewModel From(EncodingRule r) => new(r.Module ?? "", r.Logger, r.Encoding);

    public EncodingRule ToModel() => new(string.IsNullOrWhiteSpace(Module) ? null : Module.Trim(), Logger.Trim(), Encoding);
}
