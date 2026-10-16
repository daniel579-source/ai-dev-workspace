namespace LogTrace.Engine.Plants;

/// <summary>
/// 廠別設定（ADR-0006）。目前只有骨架需要的部分：辨識模組與基本的行格式。
/// JSON 讀寫、Layout、解析規則在後續 ticket 加入。
/// </summary>
public sealed record PlantConfig
{
    public required string Name { get; init; }

    /// <summary>同時間戳記時，跨檔案的模組順序（ADR-0004 第 5 點）。</summary>
    public IReadOnlyList<string> ModuleOrder { get; init; } = ["EXE", "PLC", "Server"];

    /// <summary>未提供廠別設定時使用的預設值。</summary>
    public static PlantConfig Default { get; } = new() { Name = "預設" };

    public int ModuleRank(string module)
    {
        for (int i = 0; i < ModuleOrder.Count; i++)
            if (string.Equals(ModuleOrder[i], module, StringComparison.OrdinalIgnoreCase)) return i;
        return ModuleOrder.Count;
    }
}
