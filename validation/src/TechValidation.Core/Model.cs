namespace TechValidation.Core;

/// <summary>事件旗標（ADR-0004）。</summary>
[Flags]
public enum EventFlags : int
{
    None = 0,
    TimeBackward = 1,
    SameTimestamp = 2,
    TimeInvalid = 4,
    UnknownKind = 8,
    EncodingError = 16,
}

public enum RefKind : int
{
    Command = 1,
    Pallet = 2,
}

/// <summary>一個原始 LOG 檔（含輪替檔）在案件中的描述。</summary>
public sealed class LogFile
{
    public int FileId { get; set; }
    public int Day { get; set; }               // yyyyMMdd
    public required string Module { get; init; }
    public required string Logger { get; init; }
    public required string RelativePath { get; init; } // 相對於日期資料夾的上一層
    public required string FullPath { get; init; }
    public int StreamOrder { get; set; }       // 同一條紀錄流中的順序：封存檔在前、現行檔在後
    public long Size { get; set; }
    public string Encoding { get; set; } = "";
}

/// <summary>解析後、尚未編序號的事件。</summary>
public sealed class ParsedEvent
{
    public long Ticks;            // 標準化時間（DateTime.Ticks，未套用偏移量）
    public int FileId;
    public int LineStart;         // 1 起算
    public int LineEnd;
    public long ByteOffset;
    public int ByteLength;
    public byte Level;
    public EventFlags Flags;
    public string NormText = "";
    public long[]? Commands;
    public long[]? Pallets;
    public StationChange? Station;
}

public sealed record StationChange(string Station, string Status, bool On);

public static class Levels
{
    public static byte Parse(ReadOnlySpan<byte> s) => s switch
    {
        _ when s.SequenceEqual("DEBUG"u8) => 0,
        _ when s.SequenceEqual("INFO"u8) => 1,
        _ when s.SequenceEqual("WARN"u8) => 2,
        _ when s.SequenceEqual("ERROR"u8) => 3,
        _ => 255,
    };
}
