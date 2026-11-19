namespace LogTrace.Engine.Flows;

/// <summary>
/// 一個訊號在一筆樣本中第一次出現（ADR-0024 第 11 點）：LOG 中的站點編號、訊號名稱、ON／OFF、距離派送的秒數、所在模組。
/// 只存這些摘要，不存原始 LOG 內容。角色不保存：每次依本機的 Layout 重新判斷（別名改變時可以重新分類）。
/// </summary>
public sealed record FlowSignal(string Station, string Signal, bool On, double Offset, string Module);

/// <summary>
/// 一筆正常樣本：一個正常完成的命令段落的摘要（ADR-0024 第 1、11 點）。
/// 以（來源 LOG 雜湊、命令、段號）去除重複；來源 LOG 雜湊是派送事件所在檔案的 sha256（#192 使用者決定）。
/// </summary>
public sealed record FlowSample
{
    public string SourceHash { get; init; } = "";
    public string Command { get; init; } = "";
    public int Segment { get; init; }
    /// <summary>執行這一段的設備在 LOG 中的編號，以及它的欄位種類（vehicle、rm、elevator）。</summary>
    public string Carrier { get; init; } = "";
    public string CarrierField { get; init; } = "";
    /// <summary>來源與目的地在 LOG 中的編號（來源沒有寫時，沿用上一段的目的地或命令的來源）。</summary>
    public string? From { get; init; }
    public string? To { get; init; }
    /// <summary>從派送到這一段結束（下一段派送或命令完成）的秒數。</summary>
    public double Duration { get; init; }
    /// <summary>計入樣板的時間（退回「需要重新收集」後，只使用之後計入的樣本）。</summary>
    public DateTimeOffset CollectedAt { get; init; }
    /// <summary>計入時的廠別設定修訂號。</summary>
    public int PlantRevision { get; init; }
    public IReadOnlyList<FlowSignal> Signals { get; init; } = [];

    public (string, string, int) Key => (SourceHash, Command, Segment);
}

/// <summary>
/// 一種段落保存的狀態：上次計算是否「可以判斷」、退回的時間（之前計入的樣本不再使用），
/// 以及流程改變檢查已經檢查到的計入時間（之後計入的樣本累積滿 5 筆時，和之前的樣板比對）。
/// </summary>
public sealed record FlowKindState(string Kind, bool Ready, DateTimeOffset? ResetAt, DateTimeOffset? CheckedAt = null);

/// <summary>
/// 每個廠別一份的正常流程樣板檔（<c>&lt;廠別設定檔名&gt;.flows.json</c>，放在廠別設定檔旁，不進 repo；#192 使用者決定）。
/// 比例、狀態與自我檢驗都由樣本摘要重新計算。
/// </summary>
public sealed record FlowFile
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public string Plant { get; init; } = "";
    /// <summary>最後一次計算時使用的廠別設定修訂號。</summary>
    public int PlantRevision { get; init; }
    public IReadOnlyList<FlowSample> Samples { get; init; } = [];
    public IReadOnlyList<FlowKindState> Kinds { get; init; } = [];
}

/// <summary>一種段落的狀態（ADR-0024 第 3 點）。</summary>
public enum FlowStatus
{
    Collecting,
    Ready,
    NeedsRecollect,
    /// <summary>這種設備的角色規則待確認（RM、電梯），維持「還不能判斷」。</summary>
    NotSupported,
}

/// <summary>收集進度的一列（ADR-0024 第 9 點）。</summary>
public sealed record FlowKindProgress(
    string Kind, FlowStatus Status, int Samples, int Sources, int ExpectedSignals, double? FalsePositiveRate, string Note)
{
    public string StatusText => NormalFlows.StatusText(Status);
}

/// <summary>比對結果的種類（ADR-0024 第 6 點）。</summary>
public enum FlowFindingKind
{
    /// <summary>設備少了訊號（或順序不同）。</summary>
    MissingSignal,
    /// <summary>LOG 少了資料，無法判斷，請補收 LOG。</summary>
    LogMissing,
    /// <summary>還不能判斷（收集中、對應不到 Layout、規則待確認）。</summary>
    NotYet,
    /// <summary>和正常樣板沒有差異。</summary>
    NoDifference,
}

/// <summary>一段卡住的比對結果：文字一律寫成差異，不寫成真因（ADR-0024 第 7 點）；Sequences 是事件出處。</summary>
public sealed record FlowFinding(FlowFindingKind Kind, string? SegmentKind, string Text, IReadOnlyList<long> Sequences)
{
    public string KindText => Kind switch
    {
        FlowFindingKind.MissingSignal => "設備少了訊號",
        FlowFindingKind.LogMissing => "LOG 少了資料",
        FlowFindingKind.NotYet => "還不能判斷",
        _ => "沒有差異",
    };
}

/// <summary>匯入樣板前的預覽（ADR-0024 第 11 點）。Error 不為 null 時不能匯入。</summary>
public sealed record FlowImportPreview(
    int Added, int Duplicates, IReadOnlyList<string> StatusChanges, string? RevisionNotice, string? Error, FlowFile? Merged);
