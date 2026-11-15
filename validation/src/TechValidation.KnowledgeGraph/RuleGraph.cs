using LogTrace.Engine.Events;
using LogTrace.Engine.Knowledge;
using LogTrace.Engine.Parsing;
using LogTrace.Engine.Playback;
using LogTrace.Engine.Plants;

namespace TechValidation.KnowledgeGraph;

/// <summary>
/// ADR-0015 的規則關聯圖（候選做法的驗證版）。關係只來自程式依規則可以確定的資料：
/// 佈局的輸送線上下游、LOG 記錄的搬送路徑（命令各段的來源、目的地、搬送機）、警報代碼 → 知識庫（完全比對）、
/// 安全清單（代碼或關鍵字）、異常紀錄（代碼；卡住時比對設備）。查詢時計算，不存邊表（ADR-0015 第 4 點）。
/// </summary>
public sealed class RuleGraph(
    WorldModel model, LayoutSection layout, IReadOnlyDictionary<long, DateTime> times, IReadOnlyList<LogGap> gaps,
    IReadOnlyList<Stall> stalls, KnowledgeBase kb, IReadOnlyList<SafetyRow> safety, IReadOnlyList<Incident> incidents,
    TimeSpan window, int hops)
{
    static readonly string[] ElementFields = [FieldKinds.Station, FieldKinds.Vehicle, FieldKinds.Rm, FieldKinds.Dock, FieldKinds.Elevator, FieldKinds.Source, FieldKinds.Destination];

    DateTime TimeOf(long sequence) => times[sequence];

    IEnumerable<string> ElementsOf(EventEffect e) =>
        ElementFields.SelectMany(e.GetAll).Select(code => model.Resolve(code)?.Id).OfType<string>().Distinct();

    /// <summary>佈局上下游：沿輸送線的兩端走 hops 層。</summary>
    HashSet<string> Neighbors(string device)
    {
        var found = new HashSet<string>(StringComparer.Ordinal) { device };
        var frontier = new HashSet<string>(StringComparer.Ordinal) { device };
        for (int i = 0; i < hops; i++)
        {
            var next = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in layout.Elements.Where(e => e.Type == LayoutElementType.Conveyor && e.From is not null && e.To is not null))
            {
                if (frontier.Contains(c.From!) && found.Add(c.To!)) next.Add(c.To!);
                if (frontier.Contains(c.To!) && found.Add(c.From!)) next.Add(c.From!);
            }
            frontier = next;
        }
        return found;
    }

    /// <summary>卡住時貨物所在的元素（和回放的標記一致：棧板位置，沒有時為命令的搬送機）。</summary>
    string? StallElement(Stall s)
    {
        var w = model.StateAt(s.LastSequence);
        if (s.Pallet is { } p && w.Pallets.TryGetValue(p, out var pallet) && pallet.LocationId is { } at
            && pallet.Location is LocationKind.Station or LocationKind.Vehicle or LocationKind.AtDock or LocationKind.Elevator)
            return at;
        return s.Command is { } c && w.Commands.TryGetValue(c, out var cs) ? cs.Vehicle : null;
    }

    public Found Expand(Scenario s)
    {
        var from = s.Time - window;
        var to = s.Time + window;
        bool InWindow(long seq) => TimeOf(seq) is var t && from <= t && t <= to;

        // 1. 起點：問題設備；沒有警報代碼時也以卡住的設備為起點（ADR-0015 第 3 點 2026-11-16 補充）
        var related = Neighbors(s.Device);
        var stallsHere = stalls.Where(x => x.From <= to && x.To >= from && StallElement(x) == s.Device).ToList();
        var stalled = stallsHere.Any(x => !x.OverlapsNoLog);

        // 2. LOG 記錄的搬送路徑：時間窗內經過問題設備的命令，以及卡住的命令（ADR-0015 第 2 點 2026-11-16 補充）
        var commands = model.Effects
            .Where(e => e.Get(FieldKinds.Command) is not null && InWindow(e.Sequence) && ElementsOf(e).Contains(s.Device))
            .Select(e => e.Get(FieldKinds.Command)!)
            .Concat(stallsHere.Select(x => x.Command).OfType<string>())
            .ToHashSet(StringComparer.Ordinal);
        foreach (var e in model.Effects.Where(e => e.Get(FieldKinds.Command) is { } c && commands.Contains(c)))
            related.UnionWith(ElementsOf(e));

        // 3. 時間窗內相關設備的事件與警報
        var windowEffects = model.Effects.Where(e => InWindow(e.Sequence) && ElementsOf(e).Any(related.Contains)).ToList();
        var alarms = windowEffects.Where(e => e.Effect == EffectKinds.AlarmRaised && e.Get(FieldKinds.AlarmCode) is not null)
            .SelectMany(e => ElementsOf(e).Where(related.Contains).Select(el => $"{el}|{e.Get(FieldKinds.AlarmCode)}"))
            .ToHashSet(StringComparer.Ordinal);
        var codes = alarms.Select(a => a.Split('|')[1]).ToHashSet(StringComparer.Ordinal);

        // 4. 知識庫（完全比對）、安全清單、異常紀錄
        var entries = codes.SelectMany(kb.Lookup).ToList();
        var kbFound = entries.Select(Found.KbKey).ToHashSet(StringComparer.Ordinal);
        var safetyFound = SafetyList.Match(safety, codes, [s.Question, .. entries.Select(e => e.Description)]).Select(r => r.Category).ToHashSet(StringComparer.Ordinal);
        var incidentFound = incidents.Where(i => (i.Kind == "alarm" && i.AlarmCodes.Any(codes.Contains))
                || (i.Kind == "stall" && stalled && i.Devices.Contains(s.Device)))
            .Select(i => i.Id).ToHashSet(StringComparer.Ordinal);

        // 5. 時間窗與沒有 LOG 的時段重疊時提示（ADR-0015 第 6 點）
        var noLog = gaps.Any(g => g.From < to && g.To > from);

        return new Found(kbFound, related, alarms, incidentFound, safetyFound, stalled, noLog,
            windowEffects.Count + entries.Count + incidentFound.Count + safetyFound.Count);
    }
}

/// <summary>
/// 比較基準：只用問題文字做關鍵字比對（不看 LOG）。向量搜尋（ADR-0015 第 5 點）待使用者選定模型與套件後再加，
/// 這裡先用不需要新套件的關鍵字比對代替（2026-11-16 使用者決定）。問題以空白切成詞，任一詞部分符合即算。
/// </summary>
public sealed class KeywordBaseline(LayoutSection layout, KnowledgeBase kb, IReadOnlyList<SafetyRow> safety, IReadOnlyList<Incident> incidents)
{
    public Found Find(Scenario s)
    {
        var terms = s.Question.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var entries = terms.SelectMany(kb.Search).Distinct().ToList();
        var devices = layout.Elements.Where(e => terms.Any(t => t == e.Name || (e.Aliases?.Contains(t) ?? false))).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var codes = entries.Select(e => e.Code).ToHashSet(StringComparer.Ordinal);
        var safetyFound = SafetyList.Match(safety, codes, [s.Question]).Select(r => r.Category).ToHashSet(StringComparer.Ordinal);
        var incidentFound = incidents.Where(i => i.AlarmCodes.Any(codes.Contains) || terms.Any(t => i.Symptom.Contains(t, StringComparison.Ordinal)))
            .Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        return new Found(entries.Select(Found.KbKey).ToHashSet(StringComparer.Ordinal), devices, null, incidentFound, safetyFound, null, null, null);
    }
}
