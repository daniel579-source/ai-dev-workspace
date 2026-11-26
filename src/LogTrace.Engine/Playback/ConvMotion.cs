using LogTrace.Engine.Plants;

namespace LogTrace.Engine.Playback;

/// <summary>搭配導軌的 Conv 在某一時刻的位置（ADR-0028）：中心點（樓層座標，公尺）與依據。</summary>
/// <param name="Moving">兩次對齊之間依時間內插移動中。</param>
/// <param name="Target">對齊的設備（元素 id）或儲位編號。</param>
/// <param name="Entering">true＝貨物進入時對齊前一個位置；false＝貨物離開時對齊下一個位置。</param>
/// <param name="Cause">造成這次對齊的事件序號；還沒移動過時為 null。</param>
public sealed record ConvPlacement(double X, double Y, bool Moving, string? Target, bool Entering, long? Cause)
{
    /// <summary>不在 Layout 擺放的位置（已對齊過）：一律是推估。</summary>
    public bool IsEstimated => Cause is not null;
}

/// <summary>
/// 搭配導軌的 Conv（ADR-0028）：LOG 沒有這種設備的紀錄，依貨物推估位置。貨物進入 Conv 時對齊貨物前一個位置的設備，
/// 離開時對齊下一個位置的設備；對齊點是那個設備中心（儲位取那一列的中心）在導軌上的投影，不同樓層不對齊。
/// 兩次對齊之間依時間比例內插移動；第一次對齊之前在 Layout 擺放的位置，第一次對齊直接到位。
/// </summary>
public sealed class ConvMotion
{
    sealed record Align(long Sequence, DateTime? Time, double Distance, string Target, bool Entering);

    readonly Dictionary<string, (LayoutElement Conv, RailPath Rail)> _convs = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<Align>> _aligns = new(StringComparer.Ordinal);

    /// <param name="moves">世界模型記錄的貨物移動（依事件序號）。</param>
    /// <param name="timeOf">事件序號 → 事件的時間（沒有時不內插）。</param>
    public ConvMotion(IReadOnlyList<WorldModel.PalletMove> moves, LayoutSection layout, Func<long, DateTime?> timeOf)
    {
        var floors = LayoutFloors.Of(layout);
        var byId = layout.Elements.GroupBy(e => e.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        string FloorOf(LayoutElement e) => e.Type == LayoutElementType.Elevator ? ""
            : LayoutFloors.FloorOf(e, floors, id => id is not null && byId.TryGetValue(id, out var x) ? x : null);

        foreach (var c in layout.Elements.Where(e => e.Type == LayoutElementType.Conv && e.Rail is not null))
            if (byId.TryGetValue(c.Rail!, out var r) && r.Type == LayoutElementType.Rail && (r.Points?.Count ?? 0) >= 2)
                _convs[c.Id] = (c, RailPath.Of(r));
        if (_convs.Count == 0) return;

        // 對齊點：設備的中心（電梯在每一層都有，取它的位置），儲位取那一排料架那一列的中心；不同樓層不對齊
        double? OnRail(string convId, LocationKind kind, string? id)
        {
            if (id is null) return null;
            var (conv, rail) = _convs[convId];
            (double X, double Y)? p = null;
            if (kind is LocationKind.Station or LocationKind.Vehicle or LocationKind.AtEq or LocationKind.Delivered or LocationKind.Elevator
                && byId.TryGetValue(id, out var e))
            {
                bool sameFloor = e.Type == LayoutElementType.Elevator
                    ? (e.ServedFloors ?? []).Any(f => LayoutFloors.NameComparer.Equals(f.Trim(), FloorOf(conv)))
                    : LayoutFloors.NameComparer.Equals(FloorOf(e), FloorOf(conv));
                if (sameFloor) p = ((e.X ?? 0) + (e.W ?? 0) / 2, (e.Y ?? 0) + (e.H ?? 0) / 2);
            }
            else if (kind == LocationKind.Storage && StorageCode.TryParse(layout.StorageCodeRule, id, out var slot))
            {
                var rack = layout.Elements.FirstOrDefault(r => r.Type == LayoutElementType.Rack && r.Row == slot.Row
                    && r.ColFrom <= slot.Column && slot.Column <= r.ColTo && LayoutFloors.NameComparer.Equals(FloorOf(r), FloorOf(conv)));
                if (rack is not null) p = VehicleMotion.ColumnCenter(rack, slot.Column);
            }
            return p is { } q ? rail.Project(q.X, q.Y).Distance : null;
        }

        void Add(string convId, WorldModel.PalletMove m, LocationKind kind, string? id, bool entering)
        {
            if (OnRail(convId, kind, id) is not { } d) return; // 位置不明或不同樓層：維持目前位置（6a）
            if (!_aligns.TryGetValue(convId, out var list)) _aligns[convId] = list = [];
            list.Add(new Align(m.Sequence, timeOf(m.Sequence), d, id!, entering));
        }

        foreach (var m in moves)
        {
            // 貨物進入：對齊前一個位置；離開：對齊下一個位置（5a）
            if (m.ToKind == LocationKind.Station && m.ToId is { } to && _convs.ContainsKey(to) && m.FromId != to)
                Add(to, m, m.FromKind, m.FromId, entering: true);
            if (m.FromKind == LocationKind.Station && m.FromId is { } from && _convs.ContainsKey(from) && m.ToId != from)
                Add(from, m, m.ToKind, m.ToId, entering: false);
        }
    }

    /// <summary>這個 Conv 是否搭配導軌（有指定存在的導軌）。</summary>
    public bool Moves(string convId) => _convs.ContainsKey(convId);

    /// <summary>
    /// 事件序號 sequence（含）之後、時間 time 的位置；不搭配導軌的 Conv 傳回 null。
    /// 下一次對齊在之後時，從上一次對齊的時刻開始依時間內插（7b）。
    /// </summary>
    public ConvPlacement? PlacementAt(string convId, long sequence, DateTime? time)
    {
        if (!_convs.TryGetValue(convId, out var cr)) return null;
        var (conv, rail) = cr;
        var list = _aligns.GetValueOrDefault(convId) ?? [];
        int i = list.FindLastIndex(a => a.Sequence <= sequence);
        if (i < 0) return new ConvPlacement((conv.X ?? 0) + (conv.W ?? 0) / 2, (conv.Y ?? 0) + (conv.H ?? 0) / 2, false, null, false, null);
        var a = list[i];
        if (i + 1 < list.Count && list[i + 1] is var next && a.Time is { } t0 && next.Time is { } t1 && t1 > t0 && time is { } now && now > t0)
        {
            double f = Math.Clamp((now - t0).TotalMilliseconds / (t1 - t0).TotalMilliseconds, 0, 1);
            double distance = rail.Closed && rail.Length > 0
                ? a.Distance + ((next.Distance - a.Distance) % rail.Length + rail.Length) % rail.Length * f // 依點的順序方向
                : a.Distance + (next.Distance - a.Distance) * f;
            if (f > 0 && Math.Abs(next.Distance - a.Distance) > 1e-9)
            {
                var m = rail.PointAt(distance);
                return new ConvPlacement(m.X, m.Y, true, next.Target, next.Entering, a.Sequence);
            }
        }
        var p = rail.PointAt(a.Distance);
        return new ConvPlacement(p.X, p.Y, false, a.Target, a.Entering, a.Sequence);
    }
}
