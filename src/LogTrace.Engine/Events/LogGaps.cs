namespace LogTrace.Engine.Events;

/// <summary>一個模組沒有 LOG 的時段（開始與結束都是該模組前後事件的時間，或案件的開始與結束）。</summary>
public sealed record LogGap(string Module, DateTime From, DateTime To)
{
    public TimeSpan Length => To - From;

    /// <summary>時刻落在時段中（不含兩端：兩端是該模組的事件或案件邊界）。</summary>
    public bool Contains(DateTime time) => From < time && time < To;

    /// <summary>例如「10:10:00～10:20:00，10 分鐘」。</summary>
    public string Describe() => $"{From:HH:mm:ss}～{To:HH:mm:ss}，{FormatLength(Length)}";

    public static string FormatLength(TimeSpan length) =>
        length >= TimeSpan.FromHours(1) ? $"{(int)length.TotalHours} 小時 {length.Minutes} 分鐘"
        : length >= TimeSpan.FromMinutes(1) ? $"{(int)length.TotalMinutes} 分鐘"
        : $"{(int)length.TotalSeconds} 秒";
}

/// <summary>
/// 沒有 LOG 的時段（FR-037，ADR-0008 第 3 點 2026-10-21 補充）：以模組為單位，相鄰兩筆事件的間隔超過門檻時標示；
/// 模組的第一筆比案件開始晚、最後一筆比案件結束早且超過門檻時也標示。只是「沒有 LOG」的事實，不斷定是缺漏（可能是閒置）。
/// 門檻預設 300 秒，可以依模組設定（廠別設定 gapThresholds，ADR-0006）；0 表示該模組不標示。
/// </summary>
public static class LogGaps
{
    public const int DefaultThresholdSeconds = 300;

    /// <param name="events">案件的所有事件（時間為時間軸使用的時間，也就是校正後的時間）。</param>
    /// <param name="thresholds">模組 → 秒數（模組名稱不分大小寫）；沒有列出的模組使用預設值。</param>
    /// <returns>依開始時間、模組排序。</returns>
    public static IReadOnlyList<LogGap> Find(IEnumerable<LogEvent> events, IEnumerable<KeyValuePair<string, int>>? thresholds = null)
    {
        var limits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (module, seconds) in thresholds ?? []) limits.TryAdd(module.Trim(), seconds);

        var byModule = new Dictionary<string, List<DateTime>>(StringComparer.Ordinal);
        DateTime start = DateTime.MaxValue, end = DateTime.MinValue;
        foreach (var e in events)
        {
            if (!byModule.TryGetValue(e.Module, out var times)) byModule[e.Module] = times = [];
            times.Add(e.Timestamp);
            if (e.Timestamp < start) start = e.Timestamp;
            if (e.Timestamp > end) end = e.Timestamp;
        }

        var gaps = new List<LogGap>();
        foreach (var (module, times) in byModule)
        {
            int seconds = limits.TryGetValue(module, out var s) ? s : DefaultThresholdSeconds;
            if (seconds <= 0) continue;
            var limit = TimeSpan.FromSeconds(seconds);
            times.Sort();
            var previous = start;
            foreach (var t in times.Append(end))
            {
                if (t - previous > limit) gaps.Add(new LogGap(module, previous, t));
                previous = t;
            }
        }
        return gaps.OrderBy(g => g.From).ThenBy(g => g.Module, StringComparer.Ordinal).ToList();
    }
}
