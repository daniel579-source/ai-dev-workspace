namespace LogTrace.Engine.Plants;

/// <summary>
/// 導軌的一個點（ADR-0006 第 14 點）。B 是從這一點到下一點那一段的凸度：0（或 null）＝直線；
/// 凸度 = tan(圓心角 / 4)，1＝半圓；B &gt; 0 時往行進方向的左邊彎（畫面座標，y 往下）。
/// </summary>
public sealed record RailPoint(double X, double Y, double? B = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public double Bulge => B ?? 0;

    /// <summary>凸度為 0 時存成 null（JSON 不寫出）。</summary>
    public static RailPoint Of(double x, double y, double bulge) => new(x, y, Math.Abs(bulge) < 1e-9 ? null : Math.Round(bulge, 4));
}

/// <summary>導軌上的位置：座標、離起點的路徑長度（公尺），以及（投影時）點到路徑的距離。</summary>
public readonly record struct RailPosition(double X, double Y, double Distance, double Offset = 0);

/// <summary>
/// 導軌的幾何（ADR-0006 第 14 點）：由點與各段的凸度組成，可以閉合。提供全長、依長度取位置、點投影到路徑，
/// 以及畫圖與框選用的折線。弧線以精確公式計算。供 #101（搬送機沿導軌移動）使用。
/// </summary>
public sealed class RailPath
{
    /// <summary>凸度的上限：最多半圓。</summary>
    public const double MaxBulge = 1;

    /// <summary>一段：直線（Radius 為 0）或弧線（圓心、半徑、起始角、掃過的角度，角度以畫面座標的 atan2 計）。</summary>
    readonly record struct Segment(double X0, double Y0, double X1, double Y1, double Length,
        double Cx, double Cy, double Radius, double Start, double Sweep)
    {
        public bool IsArc => Radius > 0;

        public (double X, double Y) At(double t) => IsArc
            ? (Cx + Radius * Math.Cos(Start + Sweep * t), Cy + Radius * Math.Sin(Start + Sweep * t))
            : (X0 + (X1 - X0) * t, Y0 + (Y1 - Y0) * t);
    }

    readonly List<Segment> _segments = [];

    public RailPath(IReadOnlyList<RailPoint> points, bool closed)
    {
        Points = points;
        Closed = closed;
        int n = points.Count;
        int count = n < 2 ? 0 : closed ? n : n - 1;
        for (int i = 0; i < count; i++)
            _segments.Add(Build(points[i], points[(i + 1) % n]));
        Length = _segments.Sum(s => s.Length);
    }

    public static RailPath Of(LayoutElement e) => new(e.Points ?? [], e.Closed == true);

    public IReadOnlyList<RailPoint> Points { get; }
    public bool Closed { get; }

    /// <summary>段數（閉合時最後一點接回第一點也算一段）。</summary>
    public int SegmentCount => _segments.Count;

    /// <summary>路徑全長（公尺）。</summary>
    public double Length { get; }

    static Segment Build(RailPoint a, RailPoint b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, c = Math.Sqrt(dx * dx + dy * dy);
        double bulge = Math.Clamp(a.Bulge, -MaxBulge, MaxBulge);
        if (c == 0 || Math.Abs(bulge) < 1e-9) return new Segment(a.X, a.Y, b.X, b.Y, c, 0, 0, 0, 0, 0);
        double theta = 4 * Math.Atan(Math.Abs(bulge));      // 圓心角
        double r = c / (2 * Math.Sin(theta / 2));
        double h = bulge * c / 2;                             // 弧高（帶正負）
        var (nx, ny) = (dy / c, -dx / c);                     // 行進方向的左邊（畫面座標）
        var (qx, qy) = ((a.X + b.X) / 2 + nx * h, (a.Y + b.Y) / 2 + ny * h); // 弧的中點
        double s = Math.Sign(bulge);
        var (cx, cy) = (qx - nx * s * r, qy - ny * s * r);
        double start = Math.Atan2(a.Y - cy, a.X - cx);
        return new Segment(a.X, a.Y, b.X, b.Y, r * theta, cx, cy, r, start, s * theta);
    }

    /// <summary>離起點 distance 公尺處的位置；超出範圍時閉合的路徑繞回，不閉合的取端點。</summary>
    public RailPosition PointAt(double distance)
    {
        if (_segments.Count == 0)
            return Points.Count > 0 ? new RailPosition(Points[0].X, Points[0].Y, 0) : default;
        double d = Closed && Length > 0 ? ((distance % Length) + Length) % Length : Math.Clamp(distance, 0, Length);
        double walked = 0;
        for (int i = 0; i < _segments.Count; i++)
        {
            var seg = _segments[i];
            if (d <= walked + seg.Length || i == _segments.Count - 1)
            {
                double t = seg.Length == 0 ? 0 : Math.Clamp((d - walked) / seg.Length, 0, 1);
                var (x, y) = seg.At(t);
                return new RailPosition(x, y, d);
            }
            walked += seg.Length;
        }
        return new RailPosition(Points[^1].X, Points[^1].Y, Length);
    }

    /// <summary>點 (x, y) 投影到路徑：路徑上最近的點、它離起點的長度，以及到路徑的距離。</summary>
    public RailPosition Project(double x, double y)
    {
        if (_segments.Count == 0)
            return Points.Count > 0 ? new RailPosition(Points[0].X, Points[0].Y, 0, Math.Sqrt(Sq(x - Points[0].X) + Sq(y - Points[0].Y))) : default;
        RailPosition best = default;
        double bestDist = double.MaxValue, walked = 0;
        foreach (var seg in _segments)
        {
            double t = ProjectOnSegment(seg, x, y);
            var (px, py) = seg.At(t);
            double dist = Math.Sqrt(Sq(x - px) + Sq(y - py));
            if (dist < bestDist - 1e-12)
                (bestDist, best) = (dist, new RailPosition(px, py, walked + seg.Length * t, dist));
            walked += seg.Length;
        }
        return best;
    }

    static double ProjectOnSegment(Segment seg, double x, double y)
    {
        if (!seg.IsArc)
        {
            double dx = seg.X1 - seg.X0, dy = seg.Y1 - seg.Y0, len2 = dx * dx + dy * dy;
            return len2 == 0 ? 0 : Math.Clamp(((x - seg.X0) * dx + (y - seg.Y0) * dy) / len2, 0, 1);
        }
        // 弧：點相對圓心的角度換成掃過的比例；不在弧上時取較近的端點
        double a = Math.Atan2(y - seg.Cy, x - seg.Cx);
        double rel = (a - seg.Start) * Math.Sign(seg.Sweep);
        rel = ((rel % (2 * Math.PI)) + 2 * Math.PI) % (2 * Math.PI);
        double span = Math.Abs(seg.Sweep);
        if (rel <= span) return rel / span;
        var (sx, sy) = seg.At(0);
        var (ex, ey) = seg.At(1);
        return Sq(x - sx) + Sq(y - sy) <= Sq(x - ex) + Sq(y - ey) ? 0 : 1;
    }

    static double Sq(double v) => v * v;

    /// <summary>畫圖與框選用的折線：直線取兩端，弧線切成小段（每段不超過 10 度）。</summary>
    public IReadOnlyList<(double X, double Y)> Polyline()
    {
        var list = new List<(double, double)>();
        if (_segments.Count == 0)
        {
            list.AddRange(Points.Select(p => (p.X, p.Y)));
            return list;
        }
        list.Add(_segments[0].At(0));
        foreach (var seg in _segments)
        {
            int steps = seg.IsArc ? Math.Max(2, (int)Math.Ceiling(Math.Abs(seg.Sweep) / (Math.PI / 18))) : 1;
            for (int i = 1; i <= steps; i++) list.Add(seg.At((double)i / steps));
        }
        return list;
    }

    /// <summary>每一段的中點（弧線取弧的中點）：編輯器的中點把手。</summary>
    public IReadOnlyList<(double X, double Y)> SegmentMidpoints() => _segments.Select(s => s.At(0.5)).ToList();

    /// <summary>範圍（含弧線鼓出去的部分）。</summary>
    public (double Left, double Top, double Right, double Bottom) Bounds()
    {
        var pts = Polyline();
        if (pts.Count == 0) return (0, 0, 0, 0);
        return (pts.Min(p => p.X), pts.Min(p => p.Y), pts.Max(p => p.X), pts.Max(p => p.Y));
    }

    /// <summary>任一段碰到矩形（框選用）。</summary>
    public bool TouchesRect((double Left, double Top, double Right, double Bottom) rect, double dx = 0, double dy = 0)
    {
        var pts = Polyline();
        if (pts.Count == 1) return LayoutGeometry.RectsTouch((pts[0].X + dx, pts[0].Y + dy, pts[0].X + dx, pts[0].Y + dy), rect);
        for (int i = 1; i < pts.Count; i++)
            if (LayoutGeometry.SegmentTouchesRect(pts[i - 1].X + dx, pts[i - 1].Y + dy, pts[i].X + dx, pts[i].Y + dy, rect)) return true;
        return false;
    }

    /// <summary>
    /// 弧高 h（中點離弦的距離，帶正負；正＝行進方向的左邊）換成凸度，限制在 |凸度| ≤ 1。
    /// </summary>
    public static double BulgeFromHeight(double chord, double height) =>
        chord <= 0 ? 0 : Math.Clamp(2 * height / chord, -MaxBulge, MaxBulge);

    /// <summary>點 (x, y) 在 a → b 這條弦的哪一邊、離弦多遠（正＝行進方向的左邊）。</summary>
    public static double SignedHeight(RailPoint a, RailPoint b, double x, double y)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, c = Math.Sqrt(dx * dx + dy * dy);
        if (c == 0) return 0;
        var (mx, my) = ((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        return ((x - mx) * dy - (y - my) * dx) / c;
    }

    /// <summary>第 index 段起點離路徑起點的長度。</summary>
    public double SegmentStart(int index) => _segments.Take(Math.Clamp(index, 0, _segments.Count)).Sum(s => s.Length);

    /// <summary>第 index 段的中點離路徑起點的長度。</summary>
    public double SegmentMiddle(int index) =>
        index < 0 || index >= _segments.Count ? 0 : SegmentStart(index) + _segments[index].Length / 2;

    /// <summary>
    /// 在離起點 distance 公尺處插入一點，形狀不變：直線拆成兩段直線，弧線拆成兩段同一個圓上的弧（凸度依圓心角重算）。
    /// 傳回新的點清單與新點的索引；落在既有的點上時不插入（索引為 -1）。
    /// </summary>
    public (List<RailPoint> Points, int Index) InsertAt(double distance)
    {
        var points = Points.ToList();
        if (_segments.Count == 0) return (points, -1);
        double walked = 0;
        for (int i = 0; i < _segments.Count; i++)
        {
            var seg = _segments[i];
            if (distance <= walked + seg.Length || i == _segments.Count - 1)
            {
                double t = seg.Length == 0 ? 0 : Math.Clamp((distance - walked) / seg.Length, 0, 1);
                if (t < 1e-6 || t > 1 - 1e-6) return (points, -1);
                var (x, y) = seg.At(t);
                double b1 = 0, b2 = 0;
                if (seg.IsArc)
                {
                    // 凸度 = tan(圓心角 / 4)：兩段各自依掃過的角度計算，正負與原本相同
                    double s = Math.Sign(seg.Sweep), total = Math.Abs(seg.Sweep);
                    (b1, b2) = (s * Math.Tan(total * t / 4), s * Math.Tan(total * (1 - t) / 4));
                }
                points[i] = RailPoint.Of(points[i].X, points[i].Y, b1);
                points.Insert(i + 1, RailPoint.Of(Math.Round(x, 3), Math.Round(y, 3), b2));
                return (points, i + 1);
            }
            walked += seg.Length;
        }
        return (points, -1);
    }

    /// <summary>
    /// WPF 路徑語法（公尺，平移 dx、dy）：M 起點，直線 L，弧線 A（半徑、小弧、方向）；閉合時加 Z。
    /// </summary>
    public string ToPathData(double dx = 0, double dy = 0)
    {
        static string F(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        if (Points.Count == 0) return "";
        var sb = new System.Text.StringBuilder($"M {F(Points[0].X + dx)},{F(Points[0].Y + dy)}");
        foreach (var seg in _segments)
        {
            if (seg.IsArc)
                sb.Append($" A {F(seg.Radius)},{F(seg.Radius)} 0 0 {(seg.Sweep > 0 ? 1 : 0)} {F(seg.X1 + dx)},{F(seg.Y1 + dy)}");
            else
                sb.Append($" L {F(seg.X1 + dx)},{F(seg.Y1 + dy)}");
        }
        if (Closed && _segments.Count > 0) sb.Append(" Z");
        return sb.ToString();
    }

    // ---- 範本（ADR-0006 第 14 點）：建立後就是一般的路徑 ----

    /// <summary>直線範本：從 (x, y) 往右 length 公尺。</summary>
    public static List<RailPoint> StraightTemplate(double x, double y, double length = 20) => [new(x, y), new(x + length, y)];

    /// <summary>跑道形環：上方直線從 (x, y) 往右 length 公尺，兩條直線相距 width 公尺，兩端各一個半圓（閉合）。</summary>
    public static List<RailPoint> LoopTemplate(double x, double y, double length = 20, double width = 6) =>
        [new(x, y), new(x + length, y, 1), new(x + length, y + width), new(x, y + width, 1)];

    /// <summary>所有點平移。</summary>
    public static List<RailPoint> Shift(IEnumerable<RailPoint> points, double dx, double dy) =>
        points.Select(p => p with { X = p.X + dx, Y = p.Y + dy }).ToList();

    /// <summary>以範圍的中心順時針轉 90 度（畫面座標）；旋轉不改變彎的方向，凸度不變。</summary>
    public static List<RailPoint> Rotate90(IReadOnlyList<RailPoint> points, bool closed)
    {
        var (l, t, r, b) = new RailPath(points, closed).Bounds();
        var (cx, cy) = ((l + r) / 2, (t + b) / 2);
        return points.Select(p => p with { X = Math.Round(cx - (p.Y - cy), 3), Y = Math.Round(cy + (p.X - cx), 3) }).ToList();
    }
}
