using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogTrace.Engine.Plants;

namespace LogTrace.Workspace.Layout;

// 導軌（ADR-0006 第 14 點）：輔助元素，點＋凸度的路徑，可以閉合。選取時畫出點與中點的把手；
// 拖曳點移動那一點，拖曳中點調整彎度（弧高吸附 0.5 公尺，最多半圓）。搬送機以 Rail 指定所走的導軌。
public sealed partial class LayoutEditorViewModel
{
    (LayoutElementViewModel Rail, bool IsPoint, int Index)? _railHandleDrag;

    /// <summary>拖曳整條導軌時的原始點。</summary>
    List<(LayoutElementViewModel Rail, IReadOnlyList<RailPoint> Points)>? _railDrag;

    /// <summary>選取的導軌上選取的點（刪除點用）；沒有時為 null。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RailSelectionText))]
    public partial int? SelectedRailPoint { get; set; }

    /// <summary>選取的導軌上選取的段（插入點、這段改直線用）；沒有時為 null。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RailSelectionText))]
    public partial int? SelectedRailSegment { get; set; }

    /// <summary>屬性面板顯示目前選取的點或段。</summary>
    public string RailSelectionText =>
        SelectedRailPoint is { } p ? $"已選取第 {p + 1} 點" : SelectedRailSegment is { } s ? $"已選取第 {s + 1} 段" : "點畫布上的點或中點把手以選取";

    /// <summary>屬性面板中可以選的段（第 1 段、第 2 段…；Index 從 0 算起）。</summary>
    public ObservableCollection<RailSegmentItem> RailSegments { get; } = [];

    /// <summary>搬送機的「導軌」選單：和選取的搬送機同一層的導軌。</summary>
    public ObservableCollection<LayoutElementViewModel> RailChoices { get; } = [];

    bool IsHandleDragging => _railHandleDrag is not null;

    /// <summary>元素的範圍（樓層座標）：導軌依路徑計算，其他依位置與尺寸。</summary>
    static (double Left, double Top, double Right, double Bottom) BoundsOf(LayoutElementViewModel e) =>
        e.IsRail ? e.Path().Bounds() : LayoutGeometry.Bounds(e.X, e.Y, e.W, e.H, e.Rotation);

    static (double Left, double Top, double Right, double Bottom) BoundsOf(LayoutElement e) =>
        e.Type == LayoutElementType.Rail ? RailPath.Of(e).Bounds() : LayoutGeometry.Bounds(e.X ?? 0, e.Y ?? 0, e.W ?? 0, e.H ?? 0, e.Rotation ?? 0);

    /// <summary>更新導軌在畫面上的路徑與把手（選取的導軌才有把手）、段的清單與搬送機的導軌選單。</summary>
    void RefreshRails()
    {
        foreach (var r in Elements.Where(e => e.IsRail))
        {
            var path = r.Path();
            r.RailPathData = path.ToPathData(r.OffsetX, r.OffsetY);
            var handles = new List<RailHandleViewModel>();
            if (r.IsSelected && SelectionCount == 1)
            {
                handles.AddRange(r.RailPoints.Select((p, i) => new RailHandleViewModel(r, true, i, p.X + r.OffsetX, p.Y + r.OffsetY) { IsSelected = SelectedRailPoint == i }));
                handles.AddRange(path.SegmentMidpoints().Select((m, i) => new RailHandleViewModel(r, false, i, m.X + r.OffsetX, m.Y + r.OffsetY) { IsSelected = SelectedRailSegment == i }));
            }
            if (!handles.Select(h => (h.IsPoint, h.Index, h.X, h.Y, h.IsSelected)).SequenceEqual(r.RailHandles.Select(h => (h.IsPoint, h.Index, h.X, h.Y, h.IsSelected))))
            {
                r.RailHandles.Clear();
                foreach (var h in handles) r.RailHandles.Add(h);
            }
        }
        var segments = SelectedElement is { IsRail: true } rail
            ? Enumerable.Range(0, rail.Path().SegmentCount).Select(i => new RailSegmentItem(i, $"第 {i + 1} 段")).ToList() : [];
        if (!segments.SequenceEqual(RailSegments))
        {
            RailSegments.Clear();
            foreach (var i in segments) RailSegments.Add(i);
        }
        var choices = SelectedElement is { IsVehicle: true } v
            ? Elements.Where(e => e.IsRail && SameFloor(FloorOf(e), FloorOf(v))).Order(LayoutElementTypes.ListOrder).ToList()
            : [];
        SyncOrder(RailChoices, choices);
    }

    partial void OnSelectedRailPointChanged(int? value)
    {
        if (value is not null) SelectedRailSegment = null; // 點與段一次只選一個
        RefreshRails();
    }

    partial void OnSelectedRailSegmentChanged(int? value)
    {
        if (value is not null) SelectedRailPoint = null;
        RefreshRails();
    }

    /// <summary>修改選取的導軌的點（一次算一步）。</summary>
    void SetRailPoints(LayoutElementViewModel rail, IReadOnlyList<RailPoint> points, string status)
    {
        rail.RailPoints = points;
        Status = status;
    }

    // ---- 新增（範本，ADR-0006 第 14 點：建立後就是一般的路徑） ----

    /// <summary>新增跑道形環的導軌（直線段 20 公尺、相距 6 公尺，閉合）。</summary>
    [RelayCommand]
    void AddRailLoop() => AddRail(loop: true);

    void AddRail(bool loop)
    {
        var id = NextId(LayoutElementType.Rail);
        var top = SnapTo(Elements.Where(e => !e.IsConveyor && SameFloor(FloorOf(e), CurrentFloor)).Select(e => BoundsOf(e).Bottom).DefaultIfEmpty(0).Max() + 1);
        var model = new LayoutElement
        {
            Id = id, Type = LayoutElementType.Rail, Name = $"導軌 {id[(id.IndexOf('-') + 1)..]}", Floor = CurrentFloor,
            // 跑道形的半圓往外鼓 3 公尺：起點右移 3 公尺，左邊的半圓才不會超出畫布
            Points = loop ? RailPath.LoopTemplate(4, top) : RailPath.StraightTemplate(1, top), Closed = loop ? true : null,
        };
        var vm = new LayoutElementViewModel(model);
        Attach(vm);
        SelectedElement = vm;
        IsDirty = true;
        Status = loop ? "已新增跑道形導軌。" : "已新增導軌。";
    }

    // ---- 把手 ----

    /// <summary>按下導軌的把手：選取那一點或那一段，並開始拖曳。</summary>
    public void BeginRailHandleDrag(RailHandleViewModel handle)
    {
        if (SelectedElement != handle.Rail) SelectedElement = handle.Rail;
        if (handle.IsPoint) (SelectedRailPoint, SelectedRailSegment) = (handle.Index, null);
        else (SelectedRailPoint, SelectedRailSegment) = (null, handle.Index);
        _railHandleDrag = (handle.Rail, handle.IsPoint, handle.Index);
    }

    /// <summary>
    /// 拖曳把手到 (x, y)（畫面上的公尺）：點吸附 0.5 公尺；中點依離弦的距離（吸附 0.5 公尺）算出凸度，最多半圓。
    /// </summary>
    public void DragRailHandleTo(double x, double y)
    {
        if (_railHandleDrag is not { } d) return;
        var rail = d.Rail;
        var (mx, my) = (x - rail.OffsetX, y - rail.OffsetY);
        var points = rail.RailPoints.ToList();
        if (d.Index < 0 || d.Index >= points.Count) return;
        if (d.IsPoint)
        {
            points[d.Index] = points[d.Index] with { X = Math.Max(0, SnapTo(mx)), Y = Math.Max(0, SnapTo(my)) };
        }
        else
        {
            var (a, b) = (points[d.Index], points[(d.Index + 1) % points.Count]);
            double chord = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            double h = SnapTo(RailPath.SignedHeight(a, b, mx, my));
            points[d.Index] = RailPoint.Of(a.X, a.Y, RailPath.BulgeFromHeight(chord, h));
        }
        rail.RailPoints = points;
    }

    /// <summary>在選取的段的中點插入一點。</summary>
    [RelayCommand]
    void InsertRailPoint()
    {
        if (SelectedElement is not { IsRail: true } rail || SelectedRailSegment is not { } seg) return;
        var path = rail.Path();
        var (points, index) = path.InsertAt(path.SegmentMiddle(seg));
        if (index < 0) return;
        SetRailPoints(rail, points, $"已在第 {seg + 1} 段的中點插入一點。");
        (SelectedRailPoint, SelectedRailSegment) = (index, null);
    }

    /// <summary>在畫布上雙擊導軌：在雙擊處（投影到路徑上的位置）插入一點。</summary>
    public void InsertRailPointAt(LayoutElementViewModel rail, double x, double y)
    {
        if (!rail.IsRail) return;
        SelectedElement = rail;
        var path = rail.Path();
        var (points, index) = path.InsertAt(path.Project(x - rail.OffsetX, y - rail.OffsetY).Distance);
        if (index < 0) return;
        SetRailPoints(rail, points, "已插入一點。");
        (SelectedRailPoint, SelectedRailSegment) = (index, null);
    }

    /// <summary>刪除選取的點（至少保留 2 點）；被刪的點那一段的彎度一併移除。</summary>
    [RelayCommand]
    void DeleteRailPoint()
    {
        if (SelectedElement is not { IsRail: true } rail || SelectedRailPoint is not { } i) return;
        if (rail.RailPoints.Count <= 2)
        {
            Status = "導軌至少要有 2 個點。";
            return;
        }
        var points = rail.RailPoints.ToList();
        points.RemoveAt(i);
        SelectedRailPoint = null;
        SetRailPoints(rail, points, $"已刪除第 {i + 1} 點。");
    }

    /// <summary>選取的段改成直線。</summary>
    [RelayCommand]
    void StraightenRailSegment()
    {
        if (SelectedElement is not { IsRail: true } rail || SelectedRailSegment is not { } seg || seg >= rail.RailPoints.Count) return;
        var points = rail.RailPoints.ToList();
        points[seg] = points[seg] with { B = null };
        SetRailPoints(rail, points, $"第 {seg + 1} 段已改成直線。");
    }

    /// <summary>以範圍中心順時針轉 90 度；轉完後不超出畫布左上角。</summary>
    void RotateRail(LayoutElementViewModel rail)
    {
        var points = RailPath.Rotate90(rail.RailPoints, rail.Closed);
        var (l, t, _, _) = new RailPath(points, rail.Closed).Bounds();
        if (l < 0 || t < 0) points = RailPath.Shift(points, Math.Max(0, -l), Math.Max(0, -t));
        rail.RailPoints = points;
    }

    /// <summary>
    /// 放開整條導軌：並排時依範圍中心落在哪一層換樓層，點換算成該層的座標；不超出該層的左上角（ADR-0006 第 13 點）。
    /// </summary>
    void EndRailDrag(IReadOnlyList<LayoutElementViewModel> rails)
    {
        var moves = rails.Select(r =>
        {
            var (l, t, rr, b) = r.Path().Bounds();
            var region = SideBySide ? RegionAt((l + rr) / 2 + r.OffsetX, (t + b) / 2 + r.OffsetY) : new FloorRegion(FloorOf(r), r.OffsetX, r.OffsetY, 0, 0);
            double dx = r.OffsetX - region.OffsetX, dy = r.OffsetY - region.OffsetY;
            return (Rail: r, region.Floor, Dx: Math.Max(dx, -l), Dy: Math.Max(dy, -t));
        }).ToList();
        _suppressUndo++;
        try
        {
            foreach (var m in moves)
            {
                if (!SameFloor(FloorOf(m.Rail), m.Floor)) m.Rail.Floor = m.Floor;
                if (m.Dx != 0 || m.Dy != 0) m.Rail.RailPoints = RailPath.Shift(m.Rail.RailPoints, m.Dx, m.Dy);
            }
        }
        finally
        {
            _suppressUndo--;
        }
    }

    /// <summary>搬送機的「導軌」選單旁的清除按鈕。</summary>
    [RelayCommand]
    void ClearVehicleRail()
    {
        if (SelectedElement is { IsVehicle: true } v) v.Rail = null;
    }

    /// <summary>刪除導軌時，指定它的搬送機自動清除指定（使用者決定，可以復原）。傳回清除的台數。</summary>
    int ClearRailsOf(IReadOnlySet<string> removedRailIds)
    {
        int n = 0;
        foreach (var v in Elements.Where(e => e.IsVehicle && e.Rail is { } r && removedRailIds.Contains(r)))
        {
            v.Rail = null;
            n++;
        }
        return n;
    }
}

/// <summary>屬性面板「段」選單的一項。</summary>
public sealed record RailSegmentItem(int Index, string Label);
