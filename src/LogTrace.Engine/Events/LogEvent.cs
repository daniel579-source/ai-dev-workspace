namespace LogTrace.Engine.Events;

/// <summary>事件旗標（ADR-0004 第 4 點）。</summary>
[Flags]
public enum EventFlags
{
    None = 0,
    TimeBackward = 1,
    SameTimestamp = 2,
    TimeInvalid = 4,
    UnknownKind = 8,
    EncodingError = 16,
}

/// <summary>
/// 事件的穩定識別碼（ADR-0004 第 2 點）：模組、日期資料夾、檔案相對路徑、起始行號。
/// 重新匯入後不變；事件序號則可能改變。
/// </summary>
public readonly record struct StableEventId(string Module, string DayFolder, string RelativePath, int LineStart)
{
    public override string ToString() => $"{Module}/{DayFolder}/{RelativePath}#{LineStart}";
}

/// <summary>事件在原始檔中的位置。行號從 1 起算；位元組位移從檔案開頭起算。</summary>
public readonly record struct EventSource(string RelativePath, int LineStart, int LineEnd, long ByteOffset, int ByteLength);

/// <summary>一筆事件（ADR-0004 的基本欄位）。</summary>
public sealed record LogEvent(
    long Sequence,
    StableEventId Id,
    DateTime Timestamp,
    string RawTimestamp,
    string Level,
    string Module,
    string Logger,
    string Message,
    EventSource Source,
    EventFlags Flags);
