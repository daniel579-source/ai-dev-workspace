namespace LogTrace.Engine.Plants;

/// <summary>
/// 電梯台車的閘門位置（ADR-0020 第 3 點）：與其他設備對接的側面畫閘門，其餘側面畫圍欄。
/// 側面以電梯自己的座標表示（旋轉之前）：xMin、xMax 為寬度方向的兩端，zMin、zMax 為深度方向的兩端。
/// </summary>
public static class ElevatorGates
{
    public const string XMin = "xMin", XMax = "xMax", ZMin = "zMin", ZMax = "zMax";

    /// <summary>緊鄰的距離（公尺）：Conv、站點、工站點與電梯的距離在這以內時視為對接。</summary>
    public const double Adjacent = 0.3;

    static (double X, double Y) Center(LayoutElement e) => ((e.X ?? 0) + (e.W ?? 0) / 2, (e.Y ?? 0) + (e.H ?? 0) / 2);

    /// <summary>點（樓層座標）在電梯的哪一側：轉到電梯自己的座標後，以寬、深的比例判斷。</summary>
    public static string SideFacing(LayoutElement elevator, double x, double y)
    {
        var (cx, cy) = Center(elevator);
        double t = (elevator.Rotation ?? 0) * Math.PI / 180;
        double dx = x - cx, dy = y - cy;
        double lx = dx * Math.Cos(t) + dy * Math.Sin(t);
        double lz = -dx * Math.Sin(t) + dy * Math.Cos(t);
        double hw = Math.Max((elevator.W ?? 0) / 2, 0.01), hh = Math.Max((elevator.H ?? 0) / 2, 0.01);
        return Math.Abs(lx) / hw >= Math.Abs(lz) / hh ? (lx >= 0 ? XMax : XMin) : (lz >= 0 ? ZMax : ZMin);
    }

    /// <summary>
    /// 電梯有閘門的側面：服務樓層上緊鄰的 Conv、站點、工站點所在的那一側。
    /// 輸送線不接電梯（ADR-0006），電梯只透過每層的出入口站點與其他設備交接，所以只看緊鄰的設備。
    /// </summary>
    public static IReadOnlyList<string> SidesOf(LayoutElement elevator, LayoutSection layout)
    {
        var floors = LayoutFloors.Of(layout);
        var byId = layout.Elements.GroupBy(e => e.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        LayoutElement? Find(string? id) => id is not null && byId.TryGetValue(id, out var e) ? e : null;
        var served = (elevator.ServedFloors ?? []).Select(f => f.Trim()).ToList();
        var sides = new HashSet<string>(StringComparer.Ordinal);

        var box = LayoutGeometry.Bounds(elevator.X ?? 0, elevator.Y ?? 0, elevator.W ?? 0, elevator.H ?? 0, elevator.Rotation ?? 0);
        foreach (var e in layout.Elements.Where(e => e.Type is LayoutElementType.Dock || LayoutElement.IsStationLike(e.Type)))
        {
            if (!served.Any(f => LayoutFloors.NameComparer.Equals(f, LayoutFloors.FloorOf(e, floors, Find)))) continue;
            var b = LayoutGeometry.Bounds(e.X ?? 0, e.Y ?? 0, e.W ?? 0, e.H ?? 0, e.Rotation ?? 0);
            double gapX = Math.Max(0, Math.Max(b.Left - box.Right, box.Left - b.Right));
            double gapY = Math.Max(0, Math.Max(b.Top - box.Bottom, box.Top - b.Bottom));
            if (Math.Max(gapX, gapY) > Adjacent) continue;
            var (x, y) = Center(e);
            sides.Add(SideFacing(elevator, x, y));
        }
        return [.. new[] { XMin, XMax, ZMin, ZMax }.Where(sides.Contains)];
    }
}

/// <summary>
/// EQ 的門（ADR-0020 補充）：朝緊鄰（0.3 公尺內）的 Conv；有好幾台時取最近的一台；沒有時朝元件前方（zMax，深度方向的 +y 側）。
/// 側面的表示方式與 <see cref="ElevatorGates"/> 相同。
/// </summary>
public static class EqDoors
{
    public static (string Side, string? DockId) Of(LayoutElement eq, LayoutSection layout)
    {
        var floors = LayoutFloors.Of(layout);
        var byId = layout.Elements.GroupBy(e => e.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        LayoutElement? Find(string? id) => id is not null && byId.TryGetValue(id, out var e) ? e : null;
        var floor = LayoutFloors.FloorOf(eq, floors, Find);
        var box = LayoutGeometry.Bounds(eq.X ?? 0, eq.Y ?? 0, eq.W ?? 0, eq.H ?? 0, eq.Rotation ?? 0);
        LayoutElement? best = null;
        double bestGap = double.MaxValue;
        foreach (var d in layout.Elements.Where(e => e.Type == LayoutElementType.Dock))
        {
            if (!LayoutFloors.NameComparer.Equals(LayoutFloors.FloorOf(d, floors, Find), floor)) continue;
            var b = LayoutGeometry.Bounds(d.X ?? 0, d.Y ?? 0, d.W ?? 0, d.H ?? 0, d.Rotation ?? 0);
            double gap = Math.Max(Math.Max(0, Math.Max(b.Left - box.Right, box.Left - b.Right)), Math.Max(0, Math.Max(b.Top - box.Bottom, box.Top - b.Bottom)));
            if (gap <= ElevatorGates.Adjacent && gap < bestGap) (best, bestGap) = (d, gap);
        }
        if (best is null) return (ElevatorGates.ZMax, null);
        return (ElevatorGates.SideFacing(eq, (best.X ?? 0) + (best.W ?? 0) / 2, (best.Y ?? 0) + (best.H ?? 0) / 2), best.Id);
    }
}
