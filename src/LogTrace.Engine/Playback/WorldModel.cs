using LogTrace.Engine.Parsing;
using LogTrace.Engine.Plants;

namespace LogTrace.Engine.Playback;

/// <summary>
/// 儲位編號的拆解（ADR-0006 第 4 點）：規則以字母樣式表示，R＝排、C＝列、L＝層，字數就是位數，
/// 例如 RRCCCLL 會把 0100802 拆成排 01、列 008、層 02。
/// </summary>
public static class StorageCode
{
    public static bool TryParse(string? rule, string code, out (string Row, int Column, int Level) parts)
    {
        parts = default;
        if (string.IsNullOrEmpty(rule) || code.Length != rule.Length || !code.All(char.IsAsciiDigit)) return false;
        string Take(char c) => new(code.Where((_, i) => rule[i] == c).ToArray());
        string row = Take('R'), col = Take('C'), level = Take('L');
        if (row.Length == 0 || col.Length == 0 || level.Length == 0) return false;
        parts = (row, int.Parse(col, System.Globalization.CultureInfo.InvariantCulture), int.Parse(level, System.Globalization.CultureInfo.InvariantCulture));
        return true;
    }

    public static string? Format(string? rule, string row, int column, int level)
    {
        if (string.IsNullOrEmpty(rule)) return null;
        int width(char c) => rule.Count(x => x == c);
        var r = row.PadLeft(width('R'), '0');
        var c = column.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(width('C'), '0');
        var l = level.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(width('L'), '0');
        if (r.Length != width('R') || c.Length != width('C') || l.Length != width('L')) return null;
        int ri = 0, ci = 0, li = 0;
        return new string(rule.Select(ch => ch switch { 'R' => r[ri++], 'C' => c[ci++], 'L' => l[li++], _ => ch }).ToArray());
    }
}

/// <summary>
/// 世界模型（ADR-0005）：依效果（EventEffect）從頭逐筆推導任一時刻的狀態。
/// 每隔固定筆數的效果保存一份快照；取任一時刻時，從之前最近的快照開始推導，結果與從頭推導完全相同。
/// 快照只在記憶體中，開啟案件時建立。
/// </summary>
public sealed class WorldModel
{
    public const int DefaultSnapshotEvery = 2000;

    readonly IReadOnlyList<EventEffect> _effects;
    readonly Dictionary<string, LayoutElement> _byAlias;
    readonly string? _storageRule;
    readonly string _presenceSignal;
    readonly List<(int EffectIndex, WorldState State)> _snapshots = [];

    /// <param name="effects">依事件序號排列的效果。</param>
    /// <param name="layout">廠別設定的 Layout：LOG 中的編號以別名（不分大小寫）對應到元素。</param>
    public WorldModel(IReadOnlyList<EventEffect> effects, LayoutSection layout, string presenceSignal, int snapshotEvery = DefaultSnapshotEvery)
    {
        _effects = effects;
        _storageRule = layout.StorageCodeRule;
        _presenceSignal = presenceSignal;
        _byAlias = new Dictionary<string, LayoutElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in layout.Elements)
            foreach (var a in e.Aliases ?? [])
                _byAlias.TryAdd(a.Trim(), e);

        var state = new WorldState();
        _snapshots.Add((0, state.Clone()));
        for (int i = 0; i < _effects.Count; i++)
        {
            Apply(state, _effects[i]);
            if ((i + 1) % Math.Max(1, snapshotEvery) == 0) _snapshots.Add((i + 1, state.Clone()));
        }
        Final = state;
    }

    /// <summary>案件結束時（所有事件之後）的狀態。</summary>
    public WorldState Final { get; }

    public int SnapshotCount => _snapshots.Count;

    /// <summary>會改變狀態的事件序號（依時間）。</summary>
    public IEnumerable<long> ChangeSequences => _effects.Select(e => e.Sequence).Distinct();

    /// <summary>事件序號 sequence（含）之前所有事件套用後的狀態；從最近的快照開始推導。</summary>
    public WorldState StateAt(long sequence)
    {
        int end = UpperBound(sequence);
        var (index, snapshot) = _snapshots.Last(s => s.EffectIndex <= end);
        var state = snapshot.Clone();
        for (int i = index; i < end; i++) Apply(state, _effects[i]);
        state.Sequence = Math.Max(state.Sequence, sequence);
        return state;
    }

    /// <summary>不使用快照，從頭逐筆推導（用來確認快照的結果相同）。</summary>
    public WorldState ReplayFromStart(long sequence)
    {
        var state = new WorldState();
        for (int i = 0, end = UpperBound(sequence); i < end; i++) Apply(state, _effects[i]);
        state.Sequence = Math.Max(state.Sequence, sequence);
        return state;
    }

    /// <summary>到案件結束時這個命令仍未完成：「案件範圍內未完成」（ADR-0005 第 6 點）。</summary>
    public bool IsUnfinishedInCase(string command) =>
        Final.Commands.TryGetValue(command, out var c) && !c.IsFinished;

    /// <summary>這個警報到案件結束時都沒有復歸：「未見復歸紀錄」（ADR-0005 第 5 點）。</summary>
    public bool IsAlarmNeverReset(string elementId, AlarmState alarm) =>
        Final.Alarms.TryGetValue(elementId, out var a) && a.Cause == alarm.Cause;

    /// <summary>
    /// 警報期間（ADR-0008 第 3 點：從發生到復歸；沒有復歸時 EndSequence 為 null，表示延伸到案件結束）。
    /// 只列出對應到 Layout 元素的警報。
    /// </summary>
    public IReadOnlyList<(string ElementId, string? Code, long StartSequence, long? EndSequence)> AlarmPeriods()
    {
        var open = new Dictionary<string, (string? Code, long Start)>(StringComparer.Ordinal);
        var list = new List<(string, string?, long, long?)>();
        foreach (var x in _effects)
        {
            if (x.Effect is not (EffectKinds.AlarmRaised or EffectKinds.AlarmCleared)) continue;
            var target = Resolve(x.Get(FieldKinds.Vehicle)) ?? Resolve(x.Get(FieldKinds.Station)) ?? Resolve(x.Get(FieldKinds.Dock));
            if (target is null) continue;
            if (open.Remove(target.Id, out var o)) list.Add((target.Id, o.Code, o.Start, x.Sequence));
            if (x.Effect == EffectKinds.AlarmRaised) open[target.Id] = (x.Get(FieldKinds.AlarmCode), x.Sequence);
        }
        foreach (var (id, (code, start)) in open) list.Add((id, code, start, null));
        return list.OrderBy(a => a.Item3).ToList();
    }

    /// <summary>LOG 中的編號對應到的 Layout 元素；沒有對應時為 null。</summary>
    public LayoutElement? Resolve(string? code) => code is not null && _byAlias.TryGetValue(code.Trim(), out var e) ? e : null;

    /// <summary>第一個效果序號大於 sequence 的位置。</summary>
    int UpperBound(long sequence)
    {
        int lo = 0, hi = _effects.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_effects[mid].Sequence <= sequence) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    (LocationKind Kind, string Id) Locate(string code)
    {
        if (Resolve(code) is { } e)
            return e.Type switch
            {
                LayoutElementType.Station => (LocationKind.Station, e.Id),
                LayoutElementType.Vehicle => (LocationKind.Vehicle, e.Id),
                LayoutElementType.Dock => (LocationKind.Delivered, e.Id),
                _ => (LocationKind.Unknown, code),
            };
        return StorageCode.TryParse(_storageRule, code, out _) ? (LocationKind.Storage, code) : (LocationKind.Unknown, code);
    }

    static StationState StationOf(WorldState w, string id) => w.Stations.TryGetValue(id, out var s) ? s : w.Stations[id] = new StationState();

    static VehicleState VehicleOf(WorldState w, string id) => w.Vehicles.TryGetValue(id, out var v) ? v : w.Vehicles[id] = new VehicleState();

    static CommandState CommandOf(WorldState w, string id, long cause) =>
        w.Commands.TryGetValue(id, out var c) ? c : w.Commands[id] = new CommandState { Id = id, StatusCause = cause };

    static void MovePallet(WorldState w, PalletState p, LocationKind kind, string id, long cause)
    {
        if (p.Location == LocationKind.Storage && p.LocationId is { } old && w.Storage.TryGetValue(old, out var there) && there == p.Id)
            w.Storage.Remove(old);
        (p.Location, p.LocationId, p.LocationCause) = (kind, id, cause);
        if (kind == LocationKind.Storage) w.Storage[id] = p.Id;
    }

    void Apply(WorldState w, EventEffect x)
    {
        long seq = x.Sequence;
        w.Sequence = seq;
        string? Element(string kind, LayoutElementType type) => Resolve(x.Get(kind)) is { } e && e.Type == type ? e.Id : null;

        switch (x.Effect)
        {
            case EffectKinds.Signal when Element(FieldKinds.Station, LayoutElementType.Station) is { } st && x.Get(FieldKinds.Signal) is { } name:
            {
                bool on = string.Equals(x.Get(FieldKinds.SignalValue), "ON", StringComparison.OrdinalIgnoreCase);
                var s = StationOf(w, st);
                if (string.Equals(name.Trim(), _presenceSignal, StringComparison.Ordinal)) (s.Presence, s.PresenceCause) = (on, seq);
                else s.Signals[name.Trim()] = (on, seq);
                break;
            }
            case EffectKinds.StationMode when Element(FieldKinds.Station, LayoutElementType.Station) is { } st:
                (StationOf(w, st).Mode, StationOf(w, st).ModeCause) = (x.Get(FieldKinds.StationMode), seq);
                break;

            case EffectKinds.PalletRead when x.Get(FieldKinds.Pallet) is { } id:
            {
                var (kind, at) = Locate(x.Get(FieldKinds.Station) ?? "");
                if (!w.Pallets.TryGetValue(id, out var p)) w.Pallets[id] = p = new PalletState { Id = id };
                if (x.GetAll(FieldKinds.Bin) is { Count: > 0 } bins) p.Bins = bins;
                MovePallet(w, p, kind, at, seq);
                if (kind == LocationKind.Station) StationOf(w, at);
                break;
            }
            case EffectKinds.CommandCreated when x.Get(FieldKinds.Command) is { } id:
            {
                var c = CommandOf(w, id, seq);
                c.Type = x.Get(FieldKinds.CommandType) ?? c.Type;
                c.Pallet = x.Get(FieldKinds.Pallet) ?? c.Pallet;
                c.Source = x.Get(FieldKinds.Source) ?? c.Source;
                c.Destination = x.Get(FieldKinds.Destination) ?? c.Destination;
                (c.Status, c.StatusCause) = (CommandStatus.Created, seq);
                // LOG 第一次提到這個棧板就是在命令的來源：案件開始前就已經在那裡（ADR-0005 第 2 點）
                if (c.Pallet is { } pid && !w.Pallets.ContainsKey(pid) && c.Source is { } src)
                {
                    var p = w.Pallets[pid] = new PalletState { Id = pid, Preexisting = true };
                    var (kind, at) = Locate(src);
                    MovePallet(w, p, kind, at, seq);
                }
                break;
            }
            case EffectKinds.SegmentDispatched when x.Get(FieldKinds.Command) is { } id && Element(FieldKinds.Vehicle, LayoutElementType.Vehicle) is { } vh:
            {
                var c = CommandOf(w, id, seq);
                (c.Status, c.StatusCause, c.Segment, c.Vehicle, c.SegmentDestination) =
                    (CommandStatus.SegmentRunning, seq, ParseInt(x.Get(FieldKinds.Segment)) ?? (c.Segment ?? 0) + 1, vh, x.Get(FieldKinds.Destination));
                if (c.Pallet is { } pid && w.Pallets.TryGetValue(pid, out var p)) MovePallet(w, p, LocationKind.Vehicle, vh, seq);
                var v = VehicleOf(w, vh);
                (v.Work, v.WorkCause, v.Command, v.Destination) = (VehicleWork.Executing, seq, id, x.Get(FieldKinds.Destination));
                break;
            }
            case EffectKinds.SegmentArrived when x.Get(FieldKinds.Command) is { } id && x.Get(FieldKinds.Destination) is { } dest:
            {
                var c = CommandOf(w, id, seq);
                var (kind, at) = Locate(dest);
                if (c.Pallet is { } pid && w.Pallets.TryGetValue(pid, out var p)) MovePallet(w, p, kind, at, seq);
                if (kind == LocationKind.Station) StationOf(w, at);
                (c.Status, c.StatusCause, c.Segment) = (kind == LocationKind.Delivered ? CommandStatus.Delivered : CommandStatus.SegmentArrived, seq,
                    ParseInt(x.Get(FieldKinds.Segment)) ?? c.Segment);
                var vh = Element(FieldKinds.Vehicle, LayoutElementType.Vehicle) ?? c.Vehicle;
                if (vh is not null)
                {
                    var v = VehicleOf(w, vh);
                    (v.Work, v.WorkCause, v.Command, v.Destination) = (VehicleWork.Idle, seq, null, null);
                }
                break;
            }
            case EffectKinds.CommandCompleted when x.Get(FieldKinds.Command) is { } id:
            {
                var c = CommandOf(w, id, seq);
                if (c.Status != CommandStatus.Delivered) (c.Status, c.StatusCause) = (CommandStatus.Completed, seq);
                break;
            }
            case EffectKinds.Connection when Element(FieldKinds.Vehicle, LayoutElementType.Vehicle) is { } vh:
            {
                var v = VehicleOf(w, vh);
                if (x.Get(FieldKinds.Online) is { } online) (v.Connection, v.ConnectionCause) = (online, seq);
                if (x.Get(FieldKinds.Mode) is { } mode) (v.Mode, v.ModeCause) = (mode, seq);
                break;
            }
            case EffectKinds.AlarmRaised or EffectKinds.AlarmCleared:
            {
                var target = Resolve(x.Get(FieldKinds.Vehicle)) ?? Resolve(x.Get(FieldKinds.Station)) ?? Resolve(x.Get(FieldKinds.Dock));
                if (target is null) break;
                if (x.Effect == EffectKinds.AlarmRaised) w.Alarms[target.Id] = new AlarmState(x.Get(FieldKinds.AlarmCode), seq);
                else w.Alarms.Remove(target.Id);
                break;
            }
        }
    }

    static int? ParseInt(string? s) => int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
}
