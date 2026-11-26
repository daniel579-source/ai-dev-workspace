using System.Globalization;
using LogTrace.Engine.Events;
using LogTrace.Engine.Parsing;
using LogTrace.Engine.Playback;
using LogTrace.Engine.Plants;

namespace LogTrace.Engine.Flows;

/// <summary>收集與比對需要的案件資料：世界模型、事件的時間、模組與來源檔雜湊、可能卡住與沒有 LOG 的時段。</summary>
public sealed record FlowCase(
    WorldModel Model, Func<long, DateTime?> TimeOf, Func<long, string?> ModuleOf, Func<long, string?> SourceHashOf,
    IReadOnlyList<Stall> Stalls, IReadOnlyList<LogGap> Gaps);

/// <summary>
/// 從正常命令學習的卡住比對（ADR-0024，#192）：不需要 LLM 的部分。
/// <list type="bullet">
/// <item>收集：匯入後把正常完成的命令段落（有完成回報、期間沒有警報與沒有 LOG 的時段、沒有可能卡住）計入樣板。</item>
/// <item>計算：依「同一種段落」（設備種類＋來源種類 → 目的種類）求應該出現的訊號（≥ 90% 樣本）、典型順序與自我檢驗。</item>
/// <item>比對：卡住的段落和樣板比，回報設備少了訊號、LOG 少了資料或還不能判斷；一律寫成差異，不寫成真因。</item>
/// </list>
/// </summary>
public static class NormalFlows
{
    // 「可以判斷」的條件（ADR-0024 第 4 點，初始值待依真實統計調整）
    public const int MinSamples = 5;
    public const int MinSources = 2;
    public const double ExpectedRatio = 0.9;
    public const double MaxFalsePositiveRate = 0.1;

    public const string RoleFromStation = "來源 Conv", RoleToStation = "目的 Conv",
        RoleFromAdjacent = "來源相鄰 Conv", RoleToAdjacent = "目的相鄰 Conv";

    public static string StatusText(FlowStatus s) => s switch
    {
        FlowStatus.Collecting => "收集中",
        FlowStatus.Ready => "可以判斷",
        FlowStatus.NeedsRecollect => "需要重新收集",
        _ => "還不能判斷（規則待確認）",
    };

    // ---------------- Layout：段落種類與角色 ----------------

    /// <summary>以 Layout 判斷段落種類與訊號的角色（每次以本機的設定重新判斷）。</summary>
    public sealed class LayoutIndex
    {
        readonly Dictionary<string, LayoutElement> _byAlias = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, HashSet<string>> _adjacent = new(StringComparer.Ordinal);
        readonly string? _storageRule;

        public LayoutIndex(LayoutSection layout)
        {
            _storageRule = layout.StorageCodeRule;
            foreach (var e in layout.Elements)
                foreach (var a in e.Aliases ?? [])
                    _byAlias.TryAdd(a.Trim(), e);
            var byId = layout.Elements.GroupBy(e => e.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            // EQ（其他公司的設備）本身沒有訊號，訊號在以輸送線相連的 Conv 上（ADR-0024 第 2 點補充、ADR-0026）
            foreach (var c in layout.Elements.Where(e => e.Type == LayoutElementType.Conveyor && e.From is not null && e.To is not null))
            {
                if (!byId.TryGetValue(c.From!, out var a) || !byId.TryGetValue(c.To!, out var b)) continue;
                Link(a, b);
                Link(b, a);
            }

            void Link(LayoutElement dock, LayoutElement station)
            {
                if (dock.Type != LayoutElementType.Eq || !LayoutElement.IsStationLike(station.Type)) return;
                if (!_adjacent.TryGetValue(dock.Id, out var set)) _adjacent[dock.Id] = set = new(StringComparer.Ordinal);
                set.Add(station.Id);
            }
        }

        public LayoutElement? Resolve(string? code) => code is not null && _byAlias.TryGetValue(code.Trim(), out var e) ? e : null;

        /// <summary>位置的種類名稱：Conv、EQ、儲位；對應不到時為 null。</summary>
        public string? PlaceLabel(string? code)
        {
            if (Resolve(code) is { } e)
                return LayoutElement.IsStationLike(e.Type) ? "Conv" : e.Type == LayoutElementType.Eq ? "EQ" : null;
            return code is not null && StorageCode.TryParse(_storageRule, code.Trim(), out _) ? "儲位" : null;
        }

        /// <summary>設備種類：STV、RM、電梯；對應不到時為 null。</summary>
        public static string? CarrierLabel(LayoutElement? e) => e?.Type switch
        {
            LayoutElementType.Vehicle => "STV",
            LayoutElementType.Rm => "RM",
            LayoutElementType.Elevator => "電梯",
            _ => null,
        };

        /// <summary>段落種類（例如「STV 站點 → Conv」）與無法分類的原因。</summary>
        public (string? Kind, string? Device, string? Problem) Classify(string carrier, string? from, string? to)
        {
            var device = CarrierLabel(Resolve(carrier));
            if (device is null) return (null, null, $"設備 {carrier} 對應不到 Layout");
            var a = PlaceLabel(from);
            var b = PlaceLabel(to);
            if (a is null || b is null)
                return (null, device, $"{(a is null ? $"來源 {from ?? "（沒有記錄）"}" : "")}{(a is null && b is null ? "、" : "")}{(b is null ? $"目的地 {to ?? "（沒有記錄）"}" : "")} 對應不到 Layout");
            return ($"{device} {a} → {b}", device, null);
        }

        /// <summary>站點在這一段中的角色；沒有角色時為 null（不列入比對）。</summary>
        public string? RoleOf(string station, string? from, string? to)
        {
            if (Resolve(station) is not { } s || !LayoutElement.IsStationLike(s.Type)) return null;
            var f = Resolve(from);
            var t = Resolve(to);
            if (f?.Id == s.Id) return RoleFromStation;
            if (t?.Id == s.Id) return RoleToStation;
            if (f is { Type: LayoutElementType.Eq } && _adjacent.TryGetValue(f.Id, out var fa) && fa.Contains(s.Id)) return RoleFromAdjacent;
            if (t is { Type: LayoutElementType.Eq } && _adjacent.TryGetValue(t.Id, out var ta) && ta.Contains(s.Id)) return RoleToAdjacent;
            return null;
        }
    }

    /// <summary>RM 的角色規則待確認（ADR-0024 待確認 4）；電梯的 LOG 樣式待確認（#192 使用者決定比照 RM）。</summary>
    static string? Unsupported(string? device) => device switch
    {
        "RM" => "RM 的訊號對應規則待確認（ADR-0024 待確認 4）",
        "電梯" => "電梯的 LOG 樣式待確認，比照 RM 先不判斷",
        _ => null,
    };

    // ---------------- 從案件取出命令段落 ----------------

    static readonly HashSet<string> Dispatches = [EffectKinds.SegmentDispatched, EffectKinds.ElevatorDispatched];

    /// <summary>命令的一段：派送事件、結束事件（下一段派送或命令完成，沒有時為 null）、設備、來源與目的地。</summary>
    public sealed record Segment(string Command, int Number, long DispatchSequence, long? EndSequence, bool Completed,
        string Carrier, string CarrierField, string? From, string? To);

    /// <summary>案件中所有命令的段落，依派送事件排序。</summary>
    public static IReadOnlyList<Segment> Segments(WorldModel model)
    {
        var list = new List<Segment>();
        var byCommand = model.Effects.Where(e => e.Get(FieldKinds.Command) is not null)
            .GroupBy(e => e.Get(FieldKinds.Command)!, StringComparer.Ordinal);
        foreach (var g in byCommand)
        {
            var steps = g.OrderBy(e => e.Sequence).ToList();
            var commandSource = steps.FirstOrDefault(e => e.Effect == EffectKinds.CommandCreated)?.Get(FieldKinds.Source);
            var completed = steps.FirstOrDefault(e => e.Effect == EffectKinds.CommandCompleted);
            var dispatches = steps.Where(e => Dispatches.Contains(e.Effect)).ToList();
            string? previousTo = null;
            int number = 0;
            for (int i = 0; i < dispatches.Count; i++)
            {
                var d = dispatches[i];
                number = int.TryParse(d.Get(FieldKinds.Segment), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : number + 1;
                var (carrier, field) = d.Effect == EffectKinds.ElevatorDispatched
                    ? (d.Get(FieldKinds.Elevator), FieldKinds.Elevator)
                    : d.Get(FieldKinds.Rm) is { } rm ? (rm, FieldKinds.Rm) : (d.Get(FieldKinds.Vehicle), FieldKinds.Vehicle);
                var from = d.Get(FieldKinds.Source) ?? previousTo ?? (i == 0 ? commandSource : null);
                var to = d.Get(FieldKinds.Destination);
                long? end = i + 1 < dispatches.Count ? dispatches[i + 1].Sequence
                    : completed is not null && completed.Sequence > d.Sequence ? completed.Sequence : null;
                bool done = i + 1 < dispatches.Count ? completed is not null : end is not null;
                if (carrier is not null) list.Add(new Segment(g.Key, number, d.Sequence, end, done, carrier, field, from, to));
                previousTo = to;
            }
        }
        return list.OrderBy(s => s.DispatchSequence).ToList();
    }

    /// <summary>段落期間（派送之後，到 endSequence 或 endTime 為止）有角色的站點訊號，每種訊號只取第一次出現。</summary>
    static List<(FlowSignal Signal, long Sequence)> SignalsIn(FlowCase c, LayoutIndex index, Segment s, long? endSequence, DateTime? endTime)
    {
        var start = c.TimeOf(s.DispatchSequence) ?? DateTime.MinValue;
        var seen = new HashSet<(string, string, bool)>();
        var list = new List<(FlowSignal, long)>();
        var effects = c.Model.Effects;
        int lo = 0, hi = effects.Count; // 效果依序號排列：從派送之後的第一個開始
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (effects[mid].Sequence <= s.DispatchSequence) lo = mid + 1;
            else hi = mid;
        }
        for (int i = lo; i < effects.Count; i++)
        {
            var e = effects[i];
            if (endSequence is { } es && e.Sequence > es) break;
            if (e.Effect != EffectKinds.Signal || c.TimeOf(e.Sequence) is not { } t) continue;
            if (endTime is { } et && t > et) break;
            if (e.Get(FieldKinds.Conv) is not { } station || e.Get(FieldKinds.Signal) is not { } name) continue;
            if (index.RoleOf(station, s.From, s.To) is null) continue;
            bool on = string.Equals(e.Get(FieldKinds.SignalValue), "ON", StringComparison.OrdinalIgnoreCase);
            if (!seen.Add((station.Trim().ToUpperInvariant(), name.Trim(), on))) continue;
            list.Add((new FlowSignal(station.Trim(), name.Trim(), on, Math.Round((t - start).TotalSeconds, 3), c.ModuleOf(e.Sequence) ?? ""), e.Sequence));
        }
        return list;
    }

    // ---------------- 收集 ----------------

    /// <summary>
    /// 案件中正常完成的命令段落（ADR-0024 第 1 點）：命令有完成回報；這個命令沒有被標示可能卡住；
    /// 段落期間相關設備沒有警報；段落期間沒有任何模組沒有 LOG 的時段（避免把缺 LOG 學成正常）。
    /// </summary>
    public static IReadOnlyList<FlowSample> CollectNormal(FlowCase c, LayoutSection layout, int plantRevision, DateTimeOffset now)
    {
        var index = new LayoutIndex(layout);
        var stalled = c.Stalls.Where(s => s.Command is not null).Select(s => s.Command!).ToHashSet(StringComparer.Ordinal);
        var alarms = c.Model.AlarmPeriods();
        var samples = new List<FlowSample>();
        foreach (var s in Segments(c.Model))
        {
            if (!s.Completed || s.EndSequence is not { } end || stalled.Contains(s.Command)) continue;
            if (c.SourceHashOf(s.DispatchSequence) is not { Length: > 0 } hash) continue;
            if (c.TimeOf(s.DispatchSequence) is not { } from || c.TimeOf(end) is not { } to) continue;
            if (c.Gaps.Any(g => g.From < to && g.To > from)) continue;
            var involved = new[] { index.Resolve(s.Carrier), index.Resolve(s.From), index.Resolve(s.To) }.OfType<LayoutElement>().Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
            if (alarms.Any(a => involved.Contains(a.ElementId) && a.StartSequence <= end && (a.EndSequence ?? long.MaxValue) >= s.DispatchSequence)) continue;
            samples.Add(new FlowSample
            {
                SourceHash = hash, Command = s.Command, Segment = s.Number, Carrier = s.Carrier, CarrierField = s.CarrierField,
                From = s.From, To = s.To, Duration = Math.Round((to - from).TotalSeconds, 3), CollectedAt = now, PlantRevision = plantRevision,
                Signals = SignalsIn(c, index, s, end, null).Select(x => x.Signal).ToList(),
            });
        }
        return samples;
    }

    /// <summary>把新樣本加入樣板（以來源 LOG 雜湊、命令、段號去除重複），重新計算各段落的狀態（含自動退回）。</summary>
    public static (FlowFile File, int Added, int Duplicates) Add(FlowFile? file, string plant, int plantRevision, LayoutSection layout,
        IEnumerable<FlowSample> samples, DateTimeOffset now)
    {
        file ??= new FlowFile { Plant = plant };
        var keys = file.Samples.Select(s => s.Key).ToHashSet();
        var added = new List<FlowSample>();
        int duplicates = 0;
        foreach (var s in samples)
        {
            if (keys.Add(s.Key)) added.Add(s);
            else duplicates++;
        }
        var merged = file with { Plant = plant, PlantRevision = plantRevision, Samples = [.. file.Samples, .. added] };
        return (Recalculate(merged, layout, now), added.Count, duplicates);
    }

    // ---------------- 計算 ----------------

    /// <summary>一種段落的樣板：應該出現的訊號（≥ 90%）、出現的時間範圍與模組、典型順序。</summary>
    public sealed record Template(
        string Kind, int Samples, IReadOnlyList<string> Expected, IReadOnlyDictionary<string, (double Min, double Max)> Offsets,
        IReadOnlyDictionary<string, string> Modules, IReadOnlyList<(string Before, string After)> Order);

    /// <summary>訊號在段落中的識別：角色｜訊號｜ON/OFF。</summary>
    static string KeyOf(string role, FlowSignal s) => $"{role}｜{s.Signal}｜{(s.On ? "ON" : "OFF")}";

    /// <summary>「目的站點的 置物可 ON」。</summary>
    public static string Describe(string key)
    {
        var p = key.Split('｜');
        return p.Length == 3 ? $"{p[0]} 的 {p[1]} {p[2]}" : key;
    }

    /// <summary>樣本中各訊號（以角色表示）第一次出現的秒數與模組。</summary>
    static Dictionary<string, (double Offset, string Module)> Observed(FlowSample s, LayoutIndex index)
    {
        var d = new Dictionary<string, (double, string)>(StringComparer.Ordinal);
        foreach (var x in s.Signals)
            if (index.RoleOf(x.Station, s.From, s.To) is { } role)
            {
                var key = KeyOf(role, x);
                if (!d.TryGetValue(key, out var old) || x.Offset < old.Item1) d[key] = (x.Offset, x.Module);
            }
        return d;
    }

    static bool IsExpected(int count, int n) => n > 0 && count >= ExpectedRatio * n - 1e-9;

    /// <summary>以樣本統計建立樣板。exclude 為留一比對時不計入的樣本（自我檢驗）。</summary>
    sealed class Stats
    {
        public readonly List<Dictionary<string, (double Offset, string Module)>> Samples = [];
        public readonly Dictionary<string, int> Count = new(StringComparer.Ordinal);
        public readonly Dictionary<(string, string), (int Both, int Before)> Pairs = [];
        public List<string> Candidates = [];

        public Stats(IEnumerable<Dictionary<string, (double, string)>> samples)
        {
            Samples.AddRange(samples);
            foreach (var s in Samples)
                foreach (var k in s.Keys)
                    Count[k] = Count.GetValueOrDefault(k) + 1;
            int n = Samples.Count;
            // 去掉一筆後仍可能是「應該出現」的訊號（自我檢驗用），順序只在這些訊號之間計算
            Candidates = Count.Where(kv => IsExpected(kv.Value, Math.Max(1, n - 1))).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
            foreach (var s in Samples)
                for (int i = 0; i < Candidates.Count; i++)
                    for (int j = 0; j < Candidates.Count; j++)
                    {
                        if (i == j || !s.TryGetValue(Candidates[i], out var a) || !s.TryGetValue(Candidates[j], out var b)) continue;
                        var p = Pairs.GetValueOrDefault((Candidates[i], Candidates[j]));
                        Pairs[(Candidates[i], Candidates[j])] = (p.Both + 1, p.Before + (a.Offset < b.Offset ? 1 : 0));
                    }
        }

        /// <summary>應該出現的訊號與典型順序；exclude 不為 null 時扣掉該筆樣本（留一比對）。</summary>
        public (List<string> Expected, List<(string, string)> Order) Build(Dictionary<string, (double Offset, string Module)>? exclude)
        {
            int n = Samples.Count - (exclude is null ? 0 : 1);
            var expected = Count.Where(kv => IsExpected(kv.Value - (exclude?.ContainsKey(kv.Key) == true ? 1 : 0), n))
                .Select(kv => kv.Key).ToList();
            var set = expected.ToHashSet(StringComparer.Ordinal);
            var order = new List<(string, string)>();
            foreach (var ((a, b), (both, before)) in Pairs)
            {
                if (!set.Contains(a) || !set.Contains(b)) continue;
                int bo = both, be = before;
                if (exclude is not null && exclude.TryGetValue(a, out var xa) && exclude.TryGetValue(b, out var xb))
                {
                    bo--;
                    if (xa.Offset < xb.Offset) be--;
                }
                if (bo > 0 && be >= ExpectedRatio * bo - 1e-9) order.Add((a, b));
            }
            return (expected, order);
        }
    }

    /// <summary>和樣板比：少了的訊號，以及順序不同（樣板中 A 在 B 之前，這次 B 比 A 早）。</summary>
    static (List<string> Missing, List<(string Before, string After)> Reversed) Diff(
        IReadOnlyDictionary<string, (double Offset, string Module)> observed, IEnumerable<string> expected, IEnumerable<(string, string)> order)
    {
        var missing = expected.Where(k => !observed.ContainsKey(k)).ToList();
        var reversed = order.Where(o => observed.TryGetValue(o.Item1, out var a) && observed.TryGetValue(o.Item2, out var b) && b.Offset < a.Offset).ToList();
        return (missing, reversed);
    }

    /// <summary>一種段落使用中的樣本：退回之後計入的樣本。</summary>
    static IEnumerable<FlowSample> Active(IEnumerable<FlowSample> samples, FlowKindState? state) =>
        state?.ResetAt is { } reset ? samples.Where(s => s.CollectedAt > reset) : samples;

    sealed record KindEval(string Kind, string? Device, List<FlowSample> Active, Stats Stats, double? FalsePositiveRate, bool Ready);

    static KindEval Evaluate(string kind, string? device, IEnumerable<FlowSample> all, FlowKindState? state, LayoutIndex index)
    {
        var active = Active(all, state).ToList();
        var stats = new Stats(active.Select(s => Observed(s, index)));
        double? fp = null;
        if (active.Count >= 2)
        {
            int wrong = 0;
            foreach (var s in stats.Samples)
            {
                var (expected, order) = stats.Build(s);
                var (missing, reversed) = Diff(s, expected, order);
                if (missing.Count > 0 || reversed.Count > 0) wrong++;
            }
            fp = (double)wrong / active.Count;
        }
        var full = stats.Build(null);
        bool ready = Unsupported(device) is null && active.Count >= MinSamples
            && active.Select(s => s.SourceHash).Distinct(StringComparer.Ordinal).Count() >= MinSources
            && full.Expected.Count >= 1 && fp is { } f && f <= MaxFalsePositiveRate + 1e-9;
        return new KindEval(kind, device, active, stats, fp, ready);
    }

    /// <summary>依本機的 Layout 把樣本分類（對應不到的另外列出）。</summary>
    static (Dictionary<string, (string? Device, List<FlowSample> Samples)> Kinds, List<(FlowSample Sample, string Problem)> Unmapped)
        Group(FlowFile file, LayoutIndex index)
    {
        var kinds = new Dictionary<string, (string?, List<FlowSample>)>(StringComparer.Ordinal);
        var unmapped = new List<(FlowSample, string)>();
        foreach (var s in file.Samples)
        {
            var (kind, device, problem) = index.Classify(s.Carrier, s.From, s.To);
            if (kind is null)
            {
                unmapped.Add((s, problem ?? "對應不到 Layout"));
                continue;
            }
            if (!kinds.TryGetValue(kind, out var g)) kinds[kind] = g = (device, []);
            g.Item2.Add(s);
        }
        return (kinds, unmapped);
    }

    /// <summary>
    /// 重新計算每種段落的狀態並保存：之前「可以判斷」、現在自我檢驗的誤報超過 10% 時自動退回「需要重新收集」，
    /// 之後只使用這次計入（含）以後的樣本重新建立（ADR-0024 第 5 點）。
    /// </summary>
    public static FlowFile Recalculate(FlowFile file, LayoutSection layout, DateTimeOffset now)
    {
        var index = new LayoutIndex(layout);
        var states = file.Kinds.GroupBy(k => k.Kind, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var (kinds, _) = Group(file, index);
        var result = new List<FlowKindState>();
        foreach (var (kind, (device, samples)) in kinds.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            states.TryGetValue(kind, out var state);
            var eval = Evaluate(kind, device, samples, state, index);
            DateTimeOffset? resetAt = null;
            if (state is { Ready: true })
            {
                if (!eval.Ready && eval.FalsePositiveRate > MaxFalsePositiveRate) resetAt = now.AddTicks(-1); // 自我檢驗的誤報超過 10%
                else if (Drifted(eval.Active, state.CheckedAt, index) is { } from) resetAt = from;
            }
            if (resetAt is { } r)
            {
                // 退回：之前計入的樣本不再使用，以較新的樣本重新建立
                state = state! with { Ready = false, ResetAt = r, CheckedAt = null };
                eval = Evaluate(kind, device, samples, state, index);
            }
            // 流程改變檢查從「可以判斷」之後計入的樣本開始；還沒有可以判斷時，已有的樣本都算檢查過
            var checkedAt = eval.Ready && state?.Ready == true ? Checked(eval.Active, state.CheckedAt)
                : eval.Active.Count == 0 ? state?.CheckedAt : eval.Active.Max(s => s.CollectedAt);
            result.Add(new FlowKindState(kind, eval.Ready, state?.ResetAt, checkedAt));
        }
        // 目前沒有樣本的段落（例如別名改變後）保留退回的時間
        result.AddRange(states.Values.Where(s => !kinds.ContainsKey(s.Kind) && s.ResetAt is not null).Select(s => s with { Ready = false }));
        return file with { Kinds = result };
    }

    /// <summary>
    /// 流程改變的檢查（ADR-0024 第 5 點的實作細節，Jev 0.66）：只用留一比對時，某個訊號不再出現的樣本一多，
    /// 那個訊號就不再是「應該出現」，誤報不會超過 10%。所以上次檢查之後計入的樣本累積滿 5 筆時，
    /// 和之前的樣本建立的樣板比對，誤報超過 10% 就退回；傳回退回的時間（新樣本保留），沒有改變時為 null。
    /// </summary>
    static DateTimeOffset? Drifted(List<FlowSample> active, DateTimeOffset? checkedAt, LayoutIndex index)
    {
        if (checkedAt is not { } c) return null;
        var pending = active.Where(s => s.CollectedAt > c).ToList();
        var earlier = active.Where(s => s.CollectedAt <= c).ToList();
        if (pending.Count < MinSamples || earlier.Count < MinSamples) return null;
        var (expected, order) = new Stats(earlier.Select(s => Observed(s, index))).Build(null);
        int wrong = pending.Count(s =>
        {
            var (missing, reversed) = Diff(Observed(s, index), expected, order);
            return missing.Count > 0 || reversed.Count > 0;
        });
        return (double)wrong / pending.Count > MaxFalsePositiveRate + 1e-9 ? pending.Min(s => s.CollectedAt).AddTicks(-1) : null;
    }

    /// <summary>流程改變檢查做過之後，檢查到的計入時間；新樣本不足 5 筆時維持原本的時間（累積到下一次）。</summary>
    static DateTimeOffset? Checked(List<FlowSample> active, DateTimeOffset? checkedAt)
    {
        if (checkedAt is not { } c) return active.Count == 0 ? null : active.Max(s => s.CollectedAt);
        var pending = active.Where(s => s.CollectedAt > c).ToList();
        return pending.Count >= MinSamples ? pending.Max(s => s.CollectedAt) : c;
    }

    /// <summary>收集進度（ADR-0024 第 9 點）：每種段落一列，以及對應不到 Layout 的樣本數與原因。</summary>
    public static (IReadOnlyList<FlowKindProgress> Kinds, int Unmapped, IReadOnlyList<string> UnmappedReasons) Progress(FlowFile? file, LayoutSection layout)
    {
        if (file is null) return ([], 0, []);
        var index = new LayoutIndex(layout);
        var states = file.Kinds.GroupBy(k => k.Kind, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var (kinds, unmapped) = Group(file, index);
        var rows = new List<FlowKindProgress>();
        foreach (var (kind, (device, samples)) in kinds)
        {
            states.TryGetValue(kind, out var state);
            var eval = Evaluate(kind, device, samples, state, index);
            rows.Add(ProgressOf(eval, state));
        }
        var reasons = unmapped.GroupBy(u => u.Problem, StringComparer.Ordinal).OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key}：{g.Count()} 段").ToList();
        return (rows.OrderBy(r => r.Status == FlowStatus.Ready ? 0 : 1).ThenBy(r => r.Kind, StringComparer.Ordinal).ToList(), unmapped.Count, reasons);
    }

    static FlowKindProgress ProgressOf(KindEval e, FlowKindState? state)
    {
        int sources = e.Active.Select(s => s.SourceHash).Distinct(StringComparer.Ordinal).Count();
        int expected = e.Stats.Build(null).Expected.Count;
        var status = Unsupported(e.Device) is not null ? FlowStatus.NotSupported
            : e.Ready ? FlowStatus.Ready
            : state?.ResetAt is not null ? FlowStatus.NeedsRecollect
            : FlowStatus.Collecting;
        var note = Unsupported(e.Device) ?? (e.Ready ? "" : string.Join("、", Lacking(e, sources, expected)));
        if (status == FlowStatus.NeedsRecollect) note = "自我檢驗的誤報超過 10%（流程可能改變），以較新的樣本重新收集" + (note.Length > 0 ? "；" + note : "");
        return new FlowKindProgress(e.Kind, status, e.Active.Count, sources, expected, e.FalsePositiveRate, note);
    }

    /// <summary>還沒達成的條件。</summary>
    static IEnumerable<string> Lacking(KindEval e, int sources, int expected)
    {
        if (e.Active.Count < MinSamples) yield return $"樣本 {e.Active.Count}／{MinSamples}";
        if (sources < MinSources) yield return $"來源 LOG {sources}／{MinSources}";
        if (e.Active.Count > 0 && expected == 0) yield return "沒有在 90% 以上樣本出現的訊號";
        if (e.FalsePositiveRate is { } fp && fp > MaxFalsePositiveRate + 1e-9) yield return $"自我檢驗誤報 {fp:P0}（上限 10%）";
    }

    // ---------------- 比對卡住的段落 ----------------

    /// <summary>
    /// 比對一段「可能卡住（推估）」（ADR-0024 第 6 點）：只比對命令的卡住；找出卡住時正在進行的那一段，
    /// 從派送到卡住結束（下一步或案件結束）之間的訊號和樣板比較。
    /// </summary>
    public static FlowFinding? Analyze(Stall stall, FlowCase c, FlowFile? file, LayoutSection layout)
    {
        if (stall.Command is null) return null;
        var index = new LayoutIndex(layout);
        var segment = Segments(c.Model).LastOrDefault(s => s.Command == stall.Command && s.DispatchSequence <= stall.LastSequence);
        if (segment is null) return new FlowFinding(FlowFindingKind.NotYet, null, "這個命令在卡住之前還沒有派送，沒有可以比對的段落。", [stall.LastSequence]);

        var (kind, device, problem) = index.Classify(segment.Carrier, segment.From, segment.To);
        if (kind is null)
            return new FlowFinding(FlowFindingKind.NotYet, null, $"段落對應不到 Layout（{problem}），無法比對；請在 Layout 補上別名。", [segment.DispatchSequence]);
        if (Unsupported(device) is { } why)
            return new FlowFinding(FlowFindingKind.NotYet, kind, $"「{kind}」還不能判斷：{why}。", [segment.DispatchSequence]);

        var states = (file?.Kinds ?? []).GroupBy(k => k.Kind, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        states.TryGetValue(kind, out var state);
        var samples = file is null ? [] : Group(file, index).Kinds.TryGetValue(kind, out var g) ? g.Samples : [];
        var eval = Evaluate(kind, device, samples, state, index);
        if (!eval.Ready)
        {
            var p = ProgressOf(eval, state);
            return new FlowFinding(FlowFindingKind.NotYet, kind,
                $"「{kind}」{p.StatusText}，還不能判斷：樣本 {p.Samples}／{MinSamples}、來源 LOG {p.Sources}／{MinSources}" + (p.Note.Length > 0 ? $"（{p.Note}）" : "") + "。",
                [segment.DispatchSequence]);
        }

        var (expected, order) = eval.Stats.Build(null);
        var modules = new Dictionary<string, string>(StringComparer.Ordinal);
        var offsets = new Dictionary<string, (double Min, double Max)>(StringComparer.Ordinal);
        foreach (var s in eval.Stats.Samples)
            foreach (var (k, (off, module)) in s)
            {
                modules.TryAdd(k, module);
                offsets[k] = offsets.TryGetValue(k, out var r) ? (Math.Min(r.Min, off), Math.Max(r.Max, off)) : (off, off);
            }

        var start = c.TimeOf(segment.DispatchSequence) ?? stall.LastTime;
        // LOG 少了資料：卡住與沒有 LOG 的時段重疊，或應該出現的訊號所在的模組在這段時間沒有 LOG（ADR-0008）
        var expectedModules = expected.Select(k => modules.GetValueOrDefault(k, "")).Where(m => m.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gaps = c.Gaps.Where(gp => gp.From < stall.To && gp.To > start && (stall.OverlapsNoLog || expectedModules.Contains(gp.Module))).ToList();
        if (stall.OverlapsNoLog || gaps.Count > 0)
            return new FlowFinding(FlowFindingKind.LogMissing, kind,
                "這段時間有模組沒有 LOG，無法判斷是否少了訊號；請補收 LOG 後重新匯入。"
                + string.Concat(gaps.Select(gp => $"\n  模組 {gp.Module}：{gp.Describe()}")),
                [segment.DispatchSequence]);

        var observedList = SignalsIn(c, index, segment, null, stall.To);
        var observed = new Dictionary<string, (double Offset, string Module)>(StringComparer.Ordinal);
        var sequenceOf = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (sig, seq) in observedList)
            if (index.RoleOf(sig.Station, segment.From, segment.To) is { } role && observed.TryAdd(KeyOf(role, sig), (sig.Offset, sig.Module)))
                sequenceOf[KeyOf(role, sig)] = seq;
        var (missing, reversed) = Diff(observed, expected, order);
        var n = eval.Active.Count;
        if (missing.Count == 0 && reversed.Count == 0)
            return new FlowFinding(FlowFindingKind.NoDifference, kind,
                $"和過去 {n} 次正常的「{kind}」相比，應該出現的 {expected.Count} 個訊號都有出現，順序也相同；差異不在已學到的訊號中。",
                [segment.DispatchSequence]);

        string Range(string k) => offsets.TryGetValue(k, out var r) ? $"（正常在派送後 {r.Min:0.#}～{r.Max:0.#} 秒出現）" : "";
        var lines = new List<string> { $"和過去 {n} 次正常的「{kind}」相比（這是差異，不是真因）：" };
        var sorted = missing.OrderBy(k => offsets.TryGetValue(k, out var r) ? r.Min : double.MaxValue).ThenBy(k => k, StringComparer.Ordinal).ToList();
        for (int i = 0; i < sorted.Count; i++)
            lines.Add($"  沒有出現：{Describe(sorted[i])}{Range(sorted[i])}" + (i == 0 && sorted.Count > 1 ? "（第一個沒出現的）" : ""));
        foreach (var (a, b) in reversed)
            lines.Add($"  順序不同：{Describe(a)} 正常在 {Describe(b)} 之前，這次在之後");
        var sources = new List<long> { segment.DispatchSequence };
        sources.AddRange(reversed.SelectMany(r => new[] { sequenceOf[r.Before], sequenceOf[r.After] }).Distinct());
        return new FlowFinding(FlowFindingKind.MissingSignal, kind, string.Join("\n", lines), sources);
    }

    // ---------------- 匯出、匯入與合併 ----------------

    /// <summary>
    /// 匯入另一台電腦的樣板前的預覽（ADR-0024 第 11 點）：廠別名稱不同時拒絕；以來源 LOG 雜湊等去除重複；
    /// 設定修訂號不同時提示，並一律以本機的 Layout 重新分類、重新檢驗（不沿用對方的「可以判斷」）。
    /// </summary>
    public static FlowImportPreview PreviewImport(FlowFile? local, FlowFile incoming, string plant, int plantRevision, LayoutSection layout, DateTimeOffset now)
    {
        if (!string.Equals(incoming.Plant, plant, StringComparison.Ordinal))
            return new FlowImportPreview(0, 0, [], null, $"樣板的廠別是「{incoming.Plant}」，和目前的廠別「{plant}」不同，不能匯入。", null);
        var before = Progress(local, layout).Kinds.ToDictionary(k => k.Kind, k => k.Status, StringComparer.Ordinal);
        // 對方的退回時間保留（取較晚的）；「可以判斷」只看本機的紀錄
        var kinds = (local?.Kinds ?? []).ToDictionary(k => k.Kind, k => k, StringComparer.Ordinal);
        foreach (var k in incoming.Kinds.Where(k => k.ResetAt is not null))
            kinds[k.Kind] = kinds.TryGetValue(k.Kind, out var old)
                ? old with { ResetAt = old.ResetAt is { } r && r > k.ResetAt ? r : k.ResetAt }
                : k with { Ready = false };
        var baseFile = (local ?? new FlowFile { Plant = plant }) with { Kinds = kinds.Values.ToList() };
        var (merged, added, duplicates) = Add(baseFile, plant, plantRevision, layout, incoming.Samples, now);
        var after = Progress(merged, layout).Kinds;
        var changes = after.Where(k => before.GetValueOrDefault(k.Kind, FlowStatus.Collecting) != k.Status || !before.ContainsKey(k.Kind))
            .Select(k => before.TryGetValue(k.Kind, out var b) ? $"{k.Kind}：{StatusText(b)} → {k.StatusText}" : $"{k.Kind}：新的段落，{k.StatusText}")
            .ToList();
        var notice = incoming.PlantRevision != plantRevision
            ? $"樣板的廠別設定修訂 {incoming.PlantRevision} 和本機的修訂 {plantRevision} 不同：會以本機的設定重新分類並重新檢驗。"
            : null;
        return new FlowImportPreview(added, duplicates, changes, notice, null, merged);
    }
}
