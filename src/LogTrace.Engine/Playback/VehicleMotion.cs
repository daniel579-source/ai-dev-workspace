using LogTrace.Engine.Parsing;
using LogTrace.Engine.Plants;

namespace LogTrace.Engine.Playback;

/// <summary>搬送機位置的依據（ADR-0005 第 10 點）。</summary>
public enum VehiclePositionBasis
{
    /// <summary>還沒有派送：畫在 Layout 上放的位置，位置未取得資訊。</summary>
    Unknown,
    /// <summary>派送到抵達之間，依時間比例內插（推估）；或沒有起點時直接畫在終點（推估）。</summary>
    Estimated,
    /// <summary>依 LOG 已抵達：位置為目的地在導軌上的投影。</summary>
    Arrived,
    /// <summary>執行中，但抵達時間未知（到案件結束都沒有抵達，或之後取消、作業完成）：停在起點。</summary>
    WaitingArrival,
    /// <summary>作業完成，LOG 沒有記錄地點：停在起點（#53）。</summary>
    FinishedNoLocation,
    /// <summary>命令取消：停在起點。</summary>
    Cancelled,
}

/// <summary>搬送機在某一時刻的位置：中心點（樓層座標，公尺）、依據，以及派送、抵達的事件序號。</summary>
public sealed record VehiclePlacement(double X, double Y, VehiclePositionBasis Basis, long? DispatchCause = null, long? ArrivalCause = null)
{
    public bool IsEstimated => Basis == VehiclePositionBasis.Estimated;
}

/// <summary>
/// 搬送機沿導軌移動（ADR-0005 第 10 點）：只處理有指定導軌的搬送機。LOG 沒有搬送機的位置，
/// 派送到抵達之間依時間比例內插並標為推估；抵達後依 LOG 畫在目的地在導軌上的投影。
/// 起點優先用派送的來源，其次上一個位置；目的地一律投影到導軌上最近的點（儲位取那一列的中心）；
/// 閉合的導軌依點的順序方向前進。沒有抵達、取消或作業完成時停在起點。
/// </summary>
public sealed class VehicleMotion
{
    enum LegEnd { None, Arrived, Finished, Cancelled, Superseded }

    /// <summary>一段行程：從派送開始，到抵達、作業完成、取消或下一次派送為止。距離是導軌上的長度位置。</summary>
    sealed record Leg(long DispatchSeq, DateTime? DispatchTime, double? Start, double? End,
        LegEnd EndKind, long? EndSeq, DateTime? EndTime, double? ArrivedAt);

    readonly Dictionary<string, (LayoutElement Vehicle, RailPath Rail)> _vehicles = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<Leg>> _legs = new(StringComparer.Ordinal);

    /// <param name="effects">依事件序號排列的效果。</param>
    /// <param name="timeOf">事件序號 → 事件的時間（沒有時不內插）。</param>
    public VehicleMotion(IReadOnlyList<EventEffect> effects, LayoutSection layout, Func<long, DateTime?> timeOf)
    {
        var floors = LayoutFloors.Of(layout);
        var byId = layout.Elements.GroupBy(e => e.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        string FloorOf(LayoutElement e) => LayoutFloors.FloorOf(e, floors, id => id is not null && byId.TryGetValue(id, out var x) ? x : null);
        var byAlias = new Dictionary<string, LayoutElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in layout.Elements)
            foreach (var a in e.Aliases ?? [])
                byAlias.TryAdd(a.Trim(), e);

        foreach (var v in layout.Elements.Where(e => e.Type == LayoutElementType.Vehicle && e.Rail is not null))
            if (byId.TryGetValue(v.Rail!, out var r) && r.Type == LayoutElementType.Rail && (r.Points?.Count ?? 0) >= 2)
                _vehicles[v.Id] = (v, RailPath.Of(r));

        // 目的地或來源（LOG 中的編號）在導軌上的長度位置：站點、對接設備等元素取中心；儲位取那一排料架那一列的中心
        double? OnRail(string vehicleId, string? code)
        {
            if (code is null) return null;
            var (vehicle, rail) = _vehicles[vehicleId];
            (double X, double Y)? p = null;
            if (byAlias.TryGetValue(code.Trim(), out var target) && target.Type != LayoutElementType.Rail && target.Type != LayoutElementType.Conveyor)
            {
                if (LayoutFloors.NameComparer.Equals(FloorOf(target), FloorOf(vehicle)))
                    p = ((target.X ?? 0) + (target.W ?? 0) / 2, (target.Y ?? 0) + (target.H ?? 0) / 2);
            }
            else if (StorageCode.TryParse(layout.StorageCodeRule, code.Trim(), out var slot))
            {
                var rack = layout.Elements.FirstOrDefault(e => e.Type == LayoutElementType.Rack && e.Row == slot.Row
                    && e.ColFrom <= slot.Column && slot.Column <= e.ColTo && LayoutFloors.NameComparer.Equals(FloorOf(e), FloorOf(vehicle)));
                if (rack is not null) p = ColumnCenter(rack, slot.Column);
            }
            return p is { } q ? rail.Project(q.X, q.Y).Distance : null;
        }

        // 每台搬送機依序整理成行程
        var open = new Dictionary<string, (long Seq, DateTime? Time, double? Start, double? End, string? Command)>(StringComparer.Ordinal);
        var last = new Dictionary<string, double?>(StringComparer.Ordinal); // 上一個位置
        void Close(string vh, LegEnd kind, long? seq, double? arrivedAt = null)
        {
            if (!open.Remove(vh, out var o)) return;
            if (!_legs.TryGetValue(vh, out var list)) _legs[vh] = list = [];
            list.Add(new Leg(o.Seq, o.Time, o.Start, o.End, kind, seq, seq is { } s ? timeOf(s) : null, arrivedAt));
            // 上一個位置：抵達時為抵達處；作業完成、取消、沒有抵達時停在起點；都不知道時維持原本的
            var previous = last.GetValueOrDefault(vh);
            last[vh] = kind == LegEnd.Arrived ? arrivedAt ?? o.End ?? previous : o.Start ?? previous;
        }
        string? VehicleOf(EventEffect x) =>
            x.Get(FieldKinds.Vehicle) is { } code && byAlias.TryGetValue(code.Trim(), out var e) && _vehicles.ContainsKey(e.Id) ? e.Id : null;
        string? VehicleOfCommand(string? command) =>
            command is null ? null : open.FirstOrDefault(o => o.Value.Command == command).Key;

        foreach (var x in effects)
        {
            switch (x.Effect)
            {
                case EffectKinds.SegmentDispatched when VehicleOf(x) is { } vh:
                {
                    Close(vh, LegEnd.Superseded, x.Sequence);
                    var start = OnRail(vh, x.Get(FieldKinds.Source)) ?? last.GetValueOrDefault(vh);
                    open[vh] = (x.Sequence, timeOf(x.Sequence), start, OnRail(vh, x.Get(FieldKinds.Destination)), x.Get(FieldKinds.Command));
                    break;
                }
                case EffectKinds.SegmentArrived when (VehicleOf(x) ?? VehicleOfCommand(x.Get(FieldKinds.Command))) is { } vh && open.ContainsKey(vh):
                    Close(vh, LegEnd.Arrived, x.Sequence, OnRail(vh, x.Get(FieldKinds.Destination)) ?? open[vh].End);
                    break;
                case EffectKinds.SegmentFinished when (VehicleOf(x) ?? VehicleOfCommand(x.Get(FieldKinds.Command))) is { } vh && open.ContainsKey(vh):
                    Close(vh, LegEnd.Finished, x.Sequence);
                    break;
                case EffectKinds.CommandCancelled when VehicleOfCommand(x.Get(FieldKinds.Command)) is { } vh:
                    Close(vh, LegEnd.Cancelled, x.Sequence);
                    break;
            }
        }
        foreach (var vh in open.Keys.ToList()) Close(vh, LegEnd.None, null); // 到案件結束都沒有抵達
    }

    /// <summary>這台搬送機是否沿導軌移動（有指定存在的導軌）。</summary>
    public bool Moves(string vehicleId) => _vehicles.ContainsKey(vehicleId);

    /// <summary>
    /// 事件序號 sequence（含）之後、時間 time 的位置；time 為 null 時用該事件的時間（內插比例取 0 或 1 之間的值）。
    /// 不沿導軌移動的搬送機傳回 null。
    /// </summary>
    public VehiclePlacement? PlacementAt(string vehicleId, long sequence, DateTime? time)
    {
        if (!_vehicles.TryGetValue(vehicleId, out var vr)) return null;
        var (vehicle, rail) = vr;
        var leg = _legs.TryGetValue(vehicleId, out var legs) ? legs.LastOrDefault(l => l.DispatchSeq <= sequence) : null;
        if (leg is null)
            return new VehiclePlacement((vehicle.X ?? 0) + (vehicle.W ?? 0) / 2, (vehicle.Y ?? 0) + (vehicle.H ?? 0) / 2, VehiclePositionBasis.Unknown);

        VehiclePlacement At(double? distance, VehiclePositionBasis basis, long? arrival = null)
        {
            if (distance is not { } d)
                return new VehiclePlacement((vehicle.X ?? 0) + (vehicle.W ?? 0) / 2, (vehicle.Y ?? 0) + (vehicle.H ?? 0) / 2,
                    basis == VehiclePositionBasis.Estimated ? VehiclePositionBasis.Unknown : basis, leg.DispatchSeq, arrival);
            var p = rail.PointAt(d);
            return new VehiclePlacement(p.X, p.Y, basis, leg.DispatchSeq, arrival);
        }

        bool ended = leg.EndSeq is { } end && end <= sequence;
        if (ended)
            return leg.EndKind switch
            {
                LegEnd.Arrived => At(leg.ArrivedAt ?? leg.End, VehiclePositionBasis.Arrived, leg.EndSeq),
                LegEnd.Finished => At(leg.Start, VehiclePositionBasis.FinishedNoLocation, leg.EndSeq),
                LegEnd.Cancelled => At(leg.Start, VehiclePositionBasis.Cancelled, leg.EndSeq),
                _ => At(leg.Start, VehiclePositionBasis.WaitingArrival),
            };

        // 執行中：只有之後確實抵達的行程才內插
        if (leg.EndKind != LegEnd.Arrived) return At(leg.Start, VehiclePositionBasis.WaitingArrival);
        var target = leg.ArrivedAt ?? leg.End;
        if (leg.Start is not { } s) return At(target, VehiclePositionBasis.Estimated); // 沒有起點：直接畫在終點
        if (target is not { } t) return At(s, VehiclePositionBasis.WaitingArrival);
        double f = 0;
        if (leg.DispatchTime is { } t0 && leg.EndTime is { } t1 && t1 > t0 && time is { } now)
            f = Math.Clamp((now - t0).TotalMilliseconds / (t1 - t0).TotalMilliseconds, 0, 1);
        double distance = rail.Closed && rail.Length > 0
            ? s + ((t - s) % rail.Length + rail.Length) % rail.Length * f // 依點的順序方向
            : s + (t - s) * f;
        return At(distance, VehiclePositionBasis.Estimated);
    }

    /// <summary>料架某一列的中心（考慮旋轉）：列沿長邊（寬）切分。</summary>
    static (double X, double Y) ColumnCenter(LayoutElement rack, int column)
    {
        double x = rack.X ?? 0, y = rack.Y ?? 0, w = rack.W ?? 0, h = rack.H ?? 0;
        int n = Math.Max(1, (rack.ColTo ?? 1) - (rack.ColFrom ?? 1) + 1);
        var (cx, cy) = (x + w / 2, y + h / 2);
        double dx = ((column - (rack.ColFrom ?? 1) + 0.5) * w / n) - w / 2, dy = 0; // 相對中心（未旋轉）
        (dx, dy) = (rack.Rotation ?? 0) switch
        {
            90 => (-dy, dx),
            180 => (-dx, -dy),
            270 => (dy, -dx),
            _ => (dx, dy),
        };
        return (cx + dx, cy + dy);
    }
}
