using LogTrace.Engine.Parsing;

namespace LogTrace.Workspace;

/// <summary>事件清單的篩選方式（ADR-0008 第 4 點「只看這個元素」、#23 解析欄位篩選）。</summary>
public enum FieldFilterKind
{
    /// <summary>Layout 元素：比對元素的所有別名（站點、搬送機、對接設備欄位）。</summary>
    Element,
    Command,
    Pallet,
    Bin,
}

/// <summary>篩選方式的選項。</summary>
public sealed record FieldFilterOption(FieldFilterKind Kind, string Label)
{
    public static IReadOnlyList<FieldFilterOption> All { get; } =
    [
        new(FieldFilterKind.Element, "Layout 元素"),
        new(FieldFilterKind.Command, "命令編號"),
        new(FieldFilterKind.Pallet, "棧板編號"),
        new(FieldFilterKind.Bin, "桶編號"),
    ];

    /// <summary>這種篩選比對的欄位種類。</summary>
    public IReadOnlyList<string> FieldKinds => Kind switch
    {
        FieldFilterKind.Element => Engine.Parsing.FieldKinds.Identifiers,
        FieldFilterKind.Command => [Engine.Parsing.FieldKinds.Command],
        FieldFilterKind.Pallet => [Engine.Parsing.FieldKinds.Pallet],
        _ => [Engine.Parsing.FieldKinds.Bin],
    };

    public override string ToString() => Label;
}

/// <summary>可以用來篩選的 Layout 元素（有別名的站點、搬送機、對接設備）。</summary>
public sealed record ElementChoiceViewModel(string Name, string TypeLabel, IReadOnlyList<string> Aliases)
{
    public string Display => $"{TypeLabel}　{Name}（{string.Join("・", Aliases)}）";

    public override string ToString() => Display;
}
