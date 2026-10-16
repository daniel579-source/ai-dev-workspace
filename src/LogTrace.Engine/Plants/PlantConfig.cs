namespace LogTrace.Engine.Plants;

/// <summary>
/// 依模組與 Logger 指定編碼（docs/log-format-notes.md O-002：編碼必須能依檔案設定，自動判斷只是輔助）。
/// Module 為 null 時適用所有模組；比對不分大小寫。
/// </summary>
public sealed record EncodingRule(string? Module, string Logger, string Encoding);

/// <summary>
/// 解析欄位的抽取規則（ADR-0005 第 8 點）：Pattern 是正規表示式，具名群組的名稱就是欄位種類（FieldKinds），
/// 例如 (?&lt;station&gt;…)。Module、Logger 為 null 時套用到所有模組或 Logger；比對不分大小寫。
/// Effect 不是 null 時，這一行 LOG 代表一種狀態變化（EffectKinds），參數為規則抽出的欄位（世界模型，ADR-0005）。
/// </summary>
public sealed record ParseRule(string Name, string? Module, string? Logger, string Pattern, string? Effect = null);

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

    /// <summary>解析欄位的抽取規則；匯入時套用，修改後可以重新解析。</summary>
    public IReadOnlyList<ParseRule> ParseRules { get; init; } = DefaultParseRules;

    /// <summary>
    /// 初始的抽取規則：只涵蓋 docs/log-format-notes.md 第 3、4 節在樣本中看到的格式，可以在廠別設定中調整。
    /// 警報、連線、自動／手動、儲位、桶、命令類型等格式尚未確認（待確認），不提供初始規則。
    /// </summary>
    public static IReadOnlyList<ParseRule> DefaultParseRules { get; } =
    [
        new("命令編號（8 位數字，前導 000）", null, null, @"(?<![0-9])(?<command>000[0-9]{5})(?![0-9])"),
        new("站點與設備（[A-nn] 或 [A-nn-設備]）", "PLC", null, @"^\[(?<station>[A-Z]-[0-9]{2})(?:-(?<vehicle>[A-Z]{4}[0-9]{2}))?\]"),
        new("站點狀態變化（訊號 = ON／OFF）", "PLC", null, @"^\[(?<station>[A-Z]-[0-9]{2})\]\[Station_Status_Change\] => (?<signal>.+?) = (?<signalValue>ON|OFF)\s*$",
            Parsing.EffectKinds.Signal),
        new("棧板命令（9 位數字）", null, null, @"棧板命令\s*=\s*(?<pallet>[0-9]{9})(?![0-9])"),
        new("設備（4 個英文字母＋2 位數字）", null, null, @"(?<![A-Za-z0-9])(?<vehicle>[A-Z]{4}[0-9]{2})(?![0-9])"),
    ];

    /// <summary>
    /// 代表「在席」的訊號名稱（ADR-0005 第 3 點：在席指示燈）；樣本中的站點狀態變化有「在席」（docs/log-format-notes.md）。
    /// </summary>
    public string PresenceSignal { get; init; } = "在席";

    /// <summary>個人資料的遮罩規則（正規表示式，ADR-0009 第 1 點）；預設為台灣手機號碼。</summary>
    public IReadOnlyList<string> MaskPatterns { get; init; } = Search.TextMasker.DefaultPatterns;

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
