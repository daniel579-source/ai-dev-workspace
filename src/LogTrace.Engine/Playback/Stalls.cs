using LogTrace.Engine.Events;
using LogTrace.Engine.Parsing;

namespace LogTrace.Engine.Playback;

/// <summary>可能卡住的種類（ADR-0005 第 5 點 2026-11-16 補充）。</summary>
public enum StallKind
{
    /// <summary>命令某一段派送、抵達或作業完成之後，超過門檻沒有下一步。</summary>
    CommandStep,
    /// <summary>棧板在站點被讀到後，超過門檻沒有命令派送。</summary>
    WaitingDispatch,
}

/// <summary>
/// 一段「可能卡住（推估）」：最後一次進展（LastTime，事件 LastSequence）之後，到下一次進展或案件結束（To）都沒有動靜，
/// 間隔超過門檻。標示的時段從 From（＝LastTime＋門檻）到 To。
/// OverlapsNoLog 為 true 時，等待的時段與「這段時間沒有 LOG」重疊，不判定為卡住，只提示重疊。
/// </summary>
public sealed record Stall(
    StallKind Kind, string? Command, string? Pallet, long LastSequence, DateTime LastTime, DateTime From, DateTime To,
    long? NextSequence, bool OverlapsNoLog, string Reason)
{
    /// <summary>時刻落在標示的時段中（含開始，不含結束：結束是下一次進展）；沒有下一次進展時含案件結束。</summary>
    public bool IsActiveAt(DateTime time) => From <= time && (time < To || (NextSequence is null && time == To));
}

/// <summary>
/// 找出「可能卡住（推估）」（ADR-0005 第 5 點 2026-11-16 補充，#142）。LOG 不會直接記錄卡住，所以一律是推估，不推測原因。
/// </summary>
public static class Stalls
{
    public const int DefaultThresholdSeconds = 300;

    static readonly HashSet<string> CommandSteps =
    [
        EffectKinds.SegmentDispatched, EffectKinds.SegmentArrived, EffectKinds.SegmentFinished,
        EffectKinds.ElevatorDispatched, EffectKinds.ElevatorArrived, EffectKinds.ElevatorFinished,
    ];

    static readonly HashSet<string> CommandProgress =
    [
        .. CommandSteps, EffectKinds.CommandCreated, EffectKinds.CommandCompleted, EffectKinds.CommandCancelled,
    ];

    static readonly HashSet<string> Dispatches = [EffectKinds.SegmentDispatched, EffectKinds.ElevatorDispatched];

    /// <param name="model">世界模型（用來取得命令的棧板與命令是否結束）。</param>
    /// <param name="timeOf">事件序號 → 時間（時間軸使用的時間，也就是校正後的時間）；找不到時為 null。</param>
    /// <param name="caseEnd">案件最後一筆事件的時間。</param>
    /// <param name="thresholdSeconds">門檻秒數；0 或負數表示不判定。</param>
    /// <param name="gaps">沒有 LOG 的時段（LogGaps.Find）。</param>
    /// <returns>依開始時間排序。</returns>
    public static IReadOnlyList<Stall> Find(WorldModel model, Func<long, DateTime?> timeOf, DateTime caseEnd,
        int thresholdSeconds, IReadOnlyList<LogGap> gaps)
    {
        if (thresholdSeconds <= 0) return [];
        var limit = TimeSpan.FromSeconds(thresholdSeconds);
        var found = new List<Stall>();

        void Add(StallKind kind, string? command, string? pallet, EventEffect last, DateTime lastTime, EventEffect? next, DateTime to, string what)
        {
            if (to - lastTime <= limit) return;
            var overlaps = gaps.Any(g => g.From < to && g.To > lastTime);
            var reason = $"{what}後 {LogGap.FormatLength(to - lastTime)}" + (next is null ? "到案件結束都" : "") + "沒有下一步";
            found.Add(new Stall(kind, command, pallet, last.Sequence, lastTime, lastTime + limit, to, next?.Sequence, overlaps, reason));
        }

        // (a) 命令某一段之後沒有下一步
        var byCommand = model.Effects
            .Where(e => CommandProgress.Contains(e.Effect) && e.Get(FieldKinds.Command) is not null)
            .GroupBy(e => e.Get(FieldKinds.Command)!, StringComparer.Ordinal);
        foreach (var g in byCommand)
        {
            var steps = g.OrderBy(e => e.Sequence).ToList();
            model.Final.Commands.TryGetValue(g.Key, out var final);
            int segment = 0; // LOG 沒有寫段號時，依派送的次數推算
            for (int i = 0; i < steps.Count; i++)
            {
                var e = steps[i];
                var written = int.TryParse(e.Get(FieldKinds.Segment), out var n) ? n : (int?)null;
                if (Dispatches.Contains(e.Effect)) segment = written ?? segment + 1;
                else if (written is { } w) segment = w;
                if (!CommandSteps.Contains(e.Effect) || timeOf(e.Sequence) is not { } t) continue;
                var next = i + 1 < steps.Count ? steps[i + 1] : null;
                if (next is null && final?.IsFinished == true) continue;
                var to = next is not null && timeOf(next.Sequence) is { } nt ? nt : caseEnd;
                Add(StallKind.CommandStep, g.Key, final?.Pallet, e, t, next, to, $"命令 {g.Key} {StepText(e, segment)}");
            }
        }

        // (b) 棧板在站點被讀到後沒有命令派送
        var dispatchesByPallet = new Dictionary<string, List<EventEffect>>(StringComparer.Ordinal);
        foreach (var e in model.Effects.Where(e => Dispatches.Contains(e.Effect)))
        {
            var pallet = e.Get(FieldKinds.Pallet)
                ?? (e.Get(FieldKinds.Command) is { } c && model.Final.Commands.TryGetValue(c, out var cs) ? cs.Pallet : null);
            if (pallet is null) continue;
            if (!dispatchesByPallet.TryGetValue(pallet, out var list)) dispatchesByPallet[pallet] = list = [];
            list.Add(e);
        }
        // 同一個棧板在派送前被重複讀到時，只從第一次讀到開始算
        var lastHandled = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var read in model.Effects.Where(e => e.Effect == EffectKinds.PalletRead && e.Get(FieldKinds.Pallet) is not null))
        {
            var pallet = read.Get(FieldKinds.Pallet)!;
            if (lastHandled.TryGetValue(pallet, out var until) && read.Sequence <= until) continue;
            if (timeOf(read.Sequence) is not { } t) continue;
            var next = dispatchesByPallet.TryGetValue(pallet, out var ds) ? ds.FirstOrDefault(d => d.Sequence > read.Sequence) : null;
            lastHandled[pallet] = next?.Sequence ?? long.MaxValue;
            var to = next is not null && timeOf(next.Sequence) is { } nt ? nt : caseEnd;
            Add(StallKind.WaitingDispatch, null, pallet, read, t, next, to, $"棧板 {pallet} 在 Conv {read.Get(FieldKinds.Conv)} 被讀到");
        }

        return found.OrderBy(s => s.From).ThenBy(s => s.LastSequence).ToList();
    }

    static string StepText(EventEffect e, int segment)
    {
        var text = segment > 0 ? $"第 {segment} 段" : "這一段";
        return e.Effect switch
        {
            EffectKinds.SegmentDispatched or EffectKinds.ElevatorDispatched => $"{text}派送",
            EffectKinds.SegmentArrived or EffectKinds.ElevatorArrived => $"{text}抵達",
            _ => $"{text}作業完成",
        };
    }
}
