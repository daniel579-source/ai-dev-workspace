namespace LogTrace.Engine.Plants;

/// <summary>
/// 依模組與 Logger 指定編碼（docs/log-format-notes.md O-002：編碼必須能依檔案設定，自動判斷只是輔助）。
/// Module 為 null 時適用所有模組；比對不分大小寫。
/// </summary>
public sealed record EncodingRule(string? Module, string Logger, string Encoding);

/// <summary>
/// 匯入與排序使用的廠別設定（ADR-0002 第 7 點）：從廠別設定檔（PlantConfigDocument）取出，或使用內建預設值。
/// 案件建立時保存一份在案件中。
/// </summary>
public sealed record PlantConfig
{
    public required string Name { get; init; }

    /// <summary>廠別設定檔的修訂號；內建預設值為 0。</summary>
    public int Revision { get; init; }

    /// <summary>同時間戳記時，跨檔案的模組順序（ADR-0004 第 5 點）。</summary>
    public IReadOnlyList<string> ModuleOrder { get; init; } = ["EXE", "PLC", "Server"];

    /// <summary>
    /// 已知的 LOG 種類（Logger 名稱，docs/log-format-notes.md 第 3 節）。不在清單中的 Logger，其事件標示「未知種類」
    /// （ADR-0004 第 4 點）；清單為空時不標示。
    /// </summary>
    public IReadOnlyList<string> KnownLoggers { get; init; } =
    [
        "WebServiceLOG", "EEWLOG", "EXEDebug", "DBUPDATE_LOG", "DBLOG",
        "PLCLOG", "PLCExecTimeLOG", "BCR_LOG", "WMSDebug",
        "WCSCMD", "WCSDebug",
    ];

    /// <summary>依模組與 Logger 指定的編碼；沒有指定的檔案自動判斷。</summary>
    public IReadOnlyList<EncodingRule> Encodings { get; init; } = [];

    /// <summary>沒有廠別設定檔時使用的內建預設值（不含 Layout）。</summary>
    public static PlantConfig Default { get; } = new() { Name = "內建預設" };

    public int ModuleRank(string module)
    {
        for (int i = 0; i < ModuleOrder.Count; i++)
            if (string.Equals(ModuleOrder[i], module, StringComparison.OrdinalIgnoreCase)) return i;
        return ModuleOrder.Count;
    }

    public bool IsKnownLogger(string logger) =>
        KnownLoggers.Count == 0 || KnownLoggers.Contains(logger, StringComparer.OrdinalIgnoreCase);

    /// <summary>指定給這個模組與 Logger 的編碼：模組相符的規則優先，其次是不限模組的規則；沒有指定時為 null（自動判斷）。</summary>
    public string? EncodingFor(string module, string logger)
    {
        bool Same(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        return (Encodings.FirstOrDefault(r => Same(r.Module, module) && Same(r.Logger, logger))
                ?? Encodings.FirstOrDefault(r => r.Module is null && Same(r.Logger, logger)))?.Encoding;
    }
}
