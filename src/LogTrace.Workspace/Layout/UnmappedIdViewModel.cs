using CommunityToolkit.Mvvm.ComponentModel;
using LogTrace.Engine.Parsing;
using LogTrace.Engine.Plants;

namespace LogTrace.Workspace.Layout;

/// <summary>案件 LOG 中出現、但 Layout 還沒對應的編號（ADR-0006 第 3 點）。</summary>
public sealed record UnmappedIdViewModel(string Kind, string Value, long Count)
{
    public string KindLabel => FieldKinds.Label(Kind);
}

/// <summary>解析規則的一列（ADR-0005 第 8 點）：模組、Logger 空白表示全部；效果空白表示只抽欄位。</summary>
public sealed partial class ParseRuleViewModel(string name, string module, string logger, string pattern, string effect = "") : ObservableObject
{
    /// <summary>效果的選項（第一項為空白＝只抽欄位，不代表狀態變化）。</summary>
    public static IReadOnlyList<string> EffectChoices { get; } = ["", .. EffectKinds.All.Select(e => e.Effect)];

    [ObservableProperty]
    public partial string Name { get; set; } = name;

    [ObservableProperty]
    public partial string Module { get; set; } = module;

    [ObservableProperty]
    public partial string Logger { get; set; } = logger;

    [ObservableProperty]
    public partial string Pattern { get; set; } = pattern;

    /// <summary>這一行 LOG 代表的狀態變化（世界模型，ADR-0005）；空白表示只抽欄位。</summary>
    [ObservableProperty]
    public partial string Effect { get; set; } = effect;

    public static ParseRuleViewModel From(ParseRule r) => new(r.Name, r.Module ?? "", r.Logger ?? "", r.Pattern, r.Effect ?? "");

    public ParseRule ToModel() => new(Name.Trim(),
        string.IsNullOrWhiteSpace(Module) ? null : Module.Trim(),
        string.IsNullOrWhiteSpace(Logger) ? null : Logger.Trim(),
        Pattern,
        string.IsNullOrWhiteSpace(Effect) ? null : Effect.Trim());
}
