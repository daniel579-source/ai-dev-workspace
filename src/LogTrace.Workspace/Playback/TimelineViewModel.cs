using LogTrace.Engine.Events;

namespace LogTrace.Workspace.Playback;

/// <summary>時間軸細部的一個刻度。</summary>
/// <param name="Fraction">在細部視窗中的位置（0～1）。</param>
/// <param name="IsChange">狀態變化（長刻度）；其他事件為短刻度。</param>
/// <param name="ModuleIndex">模組在案件模組順序中的位置（決定顏色）。</param>
public readonly record struct TimelineTick(double Fraction, bool IsChange, int ModuleIndex);

/// <summary>時間軸總覽上的一段警報期間（位置為 0～1）。</summary>
public readonly record struct TimelineSpan(double From, double To, string Label, bool NeverReset);

/// <summary>時間軸總覽上，一個模組沒有 LOG 的時段（位置為 0～1；Row 為模組的列）。</summary>
public readonly record struct TimelineGap(int Row, double From, double To, LogGap Gap);

/// <summary>
/// 時間軸（ADR-0008 第 3 點）。
/// <list type="bullet">
/// <item>總覽：整個案件的事件密度、警報期間（沒有復歸時延伸到案件結束）、時間不確定的位置、搜尋符合的位置、
/// 各模組沒有 LOG 的時段（每個模組一列，#84）。</item>
/// <item>細部：目前時刻前後 30 秒；狀態變化為長刻度、其他事件為短刻度，顏色代表模組。</item>
/// </list>
/// 位置一律以 0～1 表示，畫面依寬度換算；點擊或拖曳時把位置換回時間。資料改變時觸發 Changed。
/// </summary>
public sealed class TimelineViewModel
{
    /// <summary>總覽的事件密度分格數。</summary>
    public const int DensityBins = 240;

    /// <summary>細部顯示目前時刻前後幾秒。</summary>
    public static readonly TimeSpan DetailHalfWidth = TimeSpan.FromSeconds(30);

    IReadOnlyList<EventRowViewModel> _rows = [];
    HashSet<long> _changes = [];

    public event EventHandler? Changed;

    public bool HasData => _rows.Count > 0;
    public DateTime Start { get; private set; }
    public DateTime End { get; private set; }

    /// <summary>事件密度（每格的事件數除以最多的一格，0～1）。</summary>
    public IReadOnlyList<double> Density { get; private set; } = [];

    public IReadOnlyList<TimelineSpan> AlarmSpans { get; private set; } = [];

    /// <summary>時間不確定的位置：時間倒退、時間無效（時間為沿用）的事件。</summary>
    public IReadOnlyList<double> UncertainMarks { get; private set; } = [];

    /// <summary>搜尋符合的位置（與搜尋結果一致）。</summary>
    public IReadOnlyList<double> SearchMarks { get; private set; } = [];

    /// <summary>沒有 LOG 的時段的列（模組，依案件的模組順序）。</summary>
    public IReadOnlyList<string> GapRows { get; private set; } = [];

    /// <summary>各模組沒有 LOG 的時段（FR-037）。</summary>
    public IReadOnlyList<TimelineGap> Gaps { get; private set; } = [];

    /// <summary>目前時刻在總覽上的位置。</summary>
    public double Cursor { get; private set; }

    public DateTime Current { get; private set; }

    public IReadOnlyList<TimelineTick> DetailTicks { get; private set; } = [];

    public void Clear()
    {
        _rows = [];
        Density = [];
        AlarmSpans = [];
        UncertainMarks = [];
        SearchMarks = [];
        DetailTicks = [];
        GapRows = [];
        Gaps = [];
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <param name="rows">依事件序號（時間）排列的所有事件。</param>
    /// <param name="changes">會改變狀態的事件序號。</param>
    /// <param name="alarms">警報期間（開始與結束的事件序號；沒有復歸時結束為 null）。</param>
    /// <param name="modules">案件的模組（依模組順序），每個模組一列。</param>
    /// <param name="gaps">沒有 LOG 的時段（LogGaps.Find）。</param>
    public void Load(IReadOnlyList<EventRowViewModel> rows, IEnumerable<long> changes,
        IEnumerable<(string Label, long Start, long? End)> alarms,
        IReadOnlyList<string>? modules = null, IEnumerable<LogGap>? gaps = null)
    {
        _rows = rows;
        _changes = changes.ToHashSet();
        if (rows.Count == 0)
        {
            Clear();
            return;
        }
        Start = rows[0].Event.Timestamp;
        End = rows[^1].Event.Timestamp;

        var bins = new int[DensityBins];
        foreach (var r in rows) bins[Math.Min(DensityBins - 1, (int)(FractionOf(r.Event.Timestamp) * DensityBins))]++;
        int max = Math.Max(1, bins.Max());
        Density = bins.Select(b => (double)b / max).ToList();

        var bySeq = rows.ToDictionary(r => r.Sequence);
        AlarmSpans = alarms
            .Where(a => bySeq.ContainsKey(a.Start))
            .Select(a => new TimelineSpan(FractionOf(bySeq[a.Start].Event.Timestamp),
                a.End is { } e && bySeq.TryGetValue(e, out var end) ? FractionOf(end.Event.Timestamp) : 1.0, a.Label, a.End is null))
            .ToList();
        UncertainMarks = rows.Where(r => (r.Event.Flags & (EventFlags.TimeBackward | EventFlags.TimeInvalid)) != 0)
            .Select(r => FractionOf(r.Event.Timestamp)).Distinct().ToList();
        SearchMarks = [];
        GapRows = modules ?? [];
        Gaps = (gaps ?? [])
            .Select(g => new TimelineGap(GapRows.ToList().IndexOf(g.Module), FractionOf(g.From), FractionOf(g.To), g))
            .Where(g => g.Row >= 0)
            .ToList();
        SetCurrent(Start);
    }

    /// <summary>搜尋結果改變時（沒有搜尋時傳入空集合）。</summary>
    public void SetSearchMatches(IReadOnlySet<long> matches)
    {
        SearchMarks = _rows.Where(r => matches.Contains(r.Sequence)).Select(r => FractionOf(r.Event.Timestamp)).Distinct().ToList();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetCurrent(DateTime time)
    {
        Current = time;
        Cursor = FractionOf(time);
        DateTime from = time - DetailHalfWidth, to = time + DetailHalfWidth;
        var ticks = new List<TimelineTick>();
        int i = LowerBound(from);
        for (; i < _rows.Count && _rows[i].Event.Timestamp <= to; i++)
        {
            var r = _rows[i];
            ticks.Add(new TimelineTick((r.Event.Timestamp - from).Ticks / (double)(to - from).Ticks, _changes.Contains(r.Sequence), r.ModuleIndex));
        }
        DetailTicks = ticks;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>時刻落在其中的時段（不含兩端），依模組順序。</summary>
    public IReadOnlyList<LogGap> GapsAt(DateTime time) => Gaps.Where(g => g.Gap.Contains(time)).OrderBy(g => g.Row).Select(g => g.Gap).ToList();

    /// <summary>總覽上某個位置（0～1）的時段（滑鼠提示用），依模組順序。</summary>
    public IReadOnlyList<LogGap> GapsAtPosition(double fraction) =>
        Gaps.Where(g => g.From <= fraction && fraction <= g.To).OrderBy(g => g.Row).Select(g => g.Gap).ToList();

    /// <summary>滑鼠提示，例如「模組 PLC：08:10:00～08:20:00 沒有 LOG（10 分鐘；可能是閒置或缺漏）」。</summary>
    public static string Describe(LogGap gap) =>
        $"模組 {gap.Module}：{gap.From:HH:mm:ss}～{gap.To:HH:mm:ss} 沒有 LOG（{LogGap.FormatLength(gap.Length)}；可能是閒置或缺漏）";

    public double FractionOf(DateTime t) =>
        End <= Start ? 0 : Math.Clamp((t - Start).Ticks / (double)(End - Start).Ticks, 0, 1);

    /// <summary>總覽上的位置換成時間。</summary>
    public DateTime OverviewTime(double fraction) =>
        Start + TimeSpan.FromTicks((long)((End - Start).Ticks * Math.Clamp(fraction, 0, 1)));

    /// <summary>細部上的位置換成時間（以目前時刻為中心）。</summary>
    public DateTime DetailTime(double fraction) =>
        Current - DetailHalfWidth + TimeSpan.FromTicks((long)(DetailHalfWidth.Ticks * 2 * Math.Clamp(fraction, 0, 1)));

    int LowerBound(DateTime t)
    {
        int lo = 0, hi = _rows.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_rows[mid].Event.Timestamp < t) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
