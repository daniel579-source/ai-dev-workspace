namespace LogTrace.Engine.Incidents;

/// <summary>異常紀錄的來源：從案件建立，或整理過去的維修紀錄（ADR-0016 第 3 點）。</summary>
public enum IncidentSource { Case, Maintenance }

/// <summary>異常紀錄的狀態（ADR-0016 第 2、3 點）。</summary>
public enum IncidentStatus
{
    /// <summary>還沒有審核通過（新草稿、維修紀錄，或停用後恢復）。</summary>
    Draft,

    /// <summary>已審核：最新一版目前有效。</summary>
    Reviewed,

    /// <summary>修改已審核的紀錄中：舊版繼續有效，修改內容審核通過後成為新的一版。</summary>
    Revising,

    /// <summary>停用：不刪除，可以恢復（退回草稿）。</summary>
    Disabled,
}

/// <summary>
/// 紀錄引用的事件：案件中的事件序號（重新匯入後可能改變，只做顯示）、穩定識別碼（跳到事件時使用），
/// 以及加入時保存的時間、模組與遮罩後的訊息，案件不在這台電腦時也能閱讀（使用者，2026-11-13）。
/// </summary>
public sealed record IncidentEvent(long Sequence, string StableId, string Time, string Module, string Message);

/// <summary>異常紀錄一版的內容（候選欄位，ADR-0016 第 2 點）。照片或文件欄位待存放位置決定後另外處理。</summary>
public sealed record IncidentContent
{
    public string Symptom { get; init; } = "";
    /// <summary>發生的時間範圍：開始與結束（文字，沿用案件的事件時間）。</summary>
    public string OccurredFrom { get; init; } = "";
    public string OccurredTo { get; init; } = "";
    public string Plant { get; init; } = "";
    /// <summary>引用的案件名稱；維修紀錄可以空白。</summary>
    public string CaseName { get; init; } = "";
    public IReadOnlyList<IncidentEvent> Events { get; init; } = [];
    public IReadOnlyList<string> AlarmCodes { get; init; } = [];
    public IReadOnlyList<string> Devices { get; init; } = [];
    /// <summary>停機時間：自由文字（例如「約 45 分鐘」），格式待確認。</summary>
    public string Downtime { get; init; } = "";
    /// <summary>處理人：實際處理這次異常的人。</summary>
    public string Handler { get; init; } = "";
    public string Cause { get; init; } = "";
    public string Disposal { get; init; } = "";
    /// <summary>執行者：處置由誰執行（ADR-0014 第 4 點，例如「第一線」「工程師」）。</summary>
    public string Executor { get; init; } = "";
    public string Result { get; init; } = "";
    /// <summary>仍是「AI 推測」的欄位（IncidentFields 的鍵，只用於現象、原因、處置）。</summary>
    public IReadOnlyList<string> AiGuess { get; init; } = [];
}

/// <summary>一個欄位的修改：舊 → 新（以欄位的顯示文字比較）。</summary>
public sealed record FieldChange(string Field, string Old, string New);

/// <summary>審核通過的一版：完整內容、相對於前一版改過的欄位、審核者與日期、審核時的提醒。</summary>
public sealed record IncidentVersion(
    int Rev, IncidentContent Content, IReadOnlyList<FieldChange> Changes, string Reviewer, DateTimeOffset ReviewedAt, IReadOnlyList<string> Warnings);

/// <summary>經過：建立、修改、審核、修訂、停用、恢復等動作。</summary>
public sealed record IncidentHistoryEntry(DateTimeOffset At, string User, string Action, string Note);

/// <summary>
/// 一筆異常紀錄（ADR-0016）：每筆一個 JSON 檔，保存每一版與經過。
/// Work 是尚未審核的內容（草稿，或修訂中的修改）；Versions 的最後一版在已審核與修訂中時有效。
/// </summary>
public sealed record IncidentRecord
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public required string Id { get; init; }
    public IncidentSource Source { get; init; }
    public IncidentStatus Status { get; init; }
    /// <summary>撰寫者：建立紀錄的人。</summary>
    public string Author { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    /// <summary>尚未審核的內容；已審核與停用（從已審核停用）時為 null。</summary>
    public IncidentContent? Work { get; init; }
    /// <summary>進入待審核清單的時間（等待時間由此計算）；不在清單中時為 null。</summary>
    public DateTimeOffset? QueuedAt { get; init; }
    public IReadOnlyList<IncidentVersion> Versions { get; init; } = [];
    public string? DisabledReason { get; init; }
    public IReadOnlyList<IncidentHistoryEntry> History { get; init; } = [];
}
