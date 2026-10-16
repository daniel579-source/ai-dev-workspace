using System.Globalization;

namespace LogTrace.Engine.Events;

/// <summary>
/// 案件的時間偏移量（ADR-0003 第 5 點）：案件整體偏移量，加上個別模組的額外偏移量。
/// 校正後的時間＝LOG 記錄的時間＋整體偏移量＋該模組的偏移量；校正後的時間用於顯示、查詢與排序（ADR-0004 第 5 點），
/// 原始時間字串一律保留。
/// </summary>
public sealed record TimeCorrection
{
    /// <summary>偏移量的上限（正負各一天）。</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromDays(1);

    public static TimeCorrection None { get; } = new();

    /// <summary>套用到所有模組的整體偏移量。</summary>
    public TimeSpan CaseOffset { get; init; }

    /// <summary>個別模組的額外偏移量（不含 0）。</summary>
    public IReadOnlyDictionary<string, TimeSpan> ModuleOffsets { get; init; } = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);

    /// <summary>某個模組的事件實際套用的偏移量。</summary>
    public TimeSpan For(string module) => CaseOffset + (ModuleOffsets.TryGetValue(module, out var m) ? m : TimeSpan.Zero);

    public bool IsZero => CaseOffset == TimeSpan.Zero && ModuleOffsets.Values.All(v => v == TimeSpan.Zero);

    /// <summary>建立偏移量；模組偏移量為 0 的項目不保存。超出上限或不是 100 微秒的整數倍時丟出例外。</summary>
    public static TimeCorrection Create(TimeSpan caseOffset, IEnumerable<KeyValuePair<string, TimeSpan>> moduleOffsets)
    {
        Check(caseOffset);
        var modules = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
        foreach (var (module, offset) in moduleOffsets)
        {
            Check(offset);
            if (offset != TimeSpan.Zero) modules[module] = offset;
        }
        return new() { CaseOffset = caseOffset, ModuleOffsets = modules };
    }

    static void Check(TimeSpan offset)
    {
        if (offset.Duration() > Limit) throw new ArgumentOutOfRangeException(nameof(offset), "偏移量不能超過正負一天。");
        if (offset.Ticks % 1000 != 0) throw new ArgumentException("偏移量的精度是 100 微秒（0.0001 秒）。", nameof(offset));
    }

    /// <summary>偏移量的文字，例如「+5 秒」「−1.25 秒」「0」。</summary>
    public static string Format(TimeSpan offset) =>
        offset == TimeSpan.Zero ? "0"
        : (offset < TimeSpan.Zero ? "−" : "+") + offset.Duration().TotalSeconds.ToString("0.####", CultureInfo.InvariantCulture) + " 秒";

    /// <summary>解析使用者輸入的秒數（可帶正負號與小數，最多 4 位小數）；空白視為 0。無法解析時回傳 null。</summary>
    public static TimeSpan? ParseSeconds(string? text)
    {
        var t = (text ?? "").Trim().Replace('−', '-').Replace('＋', '+').Replace('－', '-');
        if (t.EndsWith('秒')) t = t[..^1].TrimEnd();
        if (t.Length == 0) return TimeSpan.Zero;
        if (!decimal.TryParse(t, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var s)) return null;
        var ticks = s * TimeSpan.TicksPerSecond;
        if (ticks != decimal.Truncate(ticks) || ticks % 1000 != 0 || Math.Abs(ticks) > TimeSpan.FromDays(1).Ticks) return null;
        return TimeSpan.FromTicks((long)ticks);
    }

    /// <summary>摘要，例如「整體 +5 秒；EXE −1.25 秒」；沒有偏移量時為「無」。</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (CaseOffset != TimeSpan.Zero) parts.Add($"整體 {Format(CaseOffset)}");
        parts.AddRange(ModuleOffsets.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => $"{m.Key} {Format(m.Value)}"));
        return parts.Count == 0 ? "無" : string.Join("；", parts);
    }

    public bool Equals(TimeCorrection? other) =>
        other is not null && CaseOffset == other.CaseOffset
        && ModuleOffsets.Count == other.ModuleOffsets.Count
        && ModuleOffsets.All(m => other.ModuleOffsets.TryGetValue(m.Key, out var v) && v == m.Value);

    public override int GetHashCode() => CaseOffset.GetHashCode() ^ ModuleOffsets.Count;
}
