namespace LogTrace.Workspace;

/// <summary>建立案件時可以選的廠別：存放位置下的廠別設定檔，或內建預設值（Path 為 null）。</summary>
public sealed record PlantChoiceViewModel(string Name, string? Path)
{
    public static PlantChoiceViewModel BuiltIn { get; } = new("內建預設（不含 Layout）", null);

    public override string ToString() => Name;
}
