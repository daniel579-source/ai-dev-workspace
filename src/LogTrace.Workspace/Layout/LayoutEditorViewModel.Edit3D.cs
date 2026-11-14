using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogTrace.Engine.Plants;

namespace LogTrace.Workspace.Layout;

// 3D 編輯（ADR-0018）：網頁把滑鼠換算成目前樓層平面上的座標（公尺），這裡沿用拖曳、吸附、復原的規則。
public sealed partial class LayoutEditorViewModel
{
    /// <summary>高度拖曳的吸附與最小值（公尺，#117 使用者決定）。</summary>
    public const double HeightSnap = 0.1;

    (LayoutElementViewModel Element, double Height)? _heightDrag;

    /// <summary>
    /// 調整大小（四角的把手，ADR-0018 第 8 點）：sx、sy 是拖曳的角在元素本身（未旋轉）座標中的方向（−1＝左／上，＋1＝右／下），
    /// (dx, dy) 是從開始拖曳的位移（樓層座標，公尺）。拖哪一角就移動那一角，對角不動；寬、深吸附到格線，最小一格。
    /// </summary>
    public void DragResizeCornerBy(int sx, int sy, double dx, double dy)
    {
        if (_drag?.FirstOrDefault(x => x.Element == _dragPrimary) is not { Element: not null } d) return;
        var e = d.Element;
        // 樓層座標的位移轉成元素本身的座標（元素以中心順時針旋轉 rotation 度，y 向下）
        double rad = -e.Rotation * Math.PI / 180, cos = Math.Round(Math.Cos(rad)), sin = Math.Round(Math.Sin(rad));
        double lx = dx * cos - dy * sin, ly = dx * sin + dy * cos;
        double w = Math.Max(Snap, SnapTo(d.W + Math.Sign(sx) * lx));
        double h = Math.Max(Snap, SnapTo(d.H + Math.Sign(sy) * ly));
        // 中心往拖曳的方向移動一半的變化量，再轉回樓層座標
        double cx = Math.Sign(sx) * (w - d.W) / 2, cy = Math.Sign(sy) * (h - d.H) / 2;
        double wx = cx * cos + cy * sin, wy = -cx * sin + cy * cos;
        double centerX = d.X + d.W / 2 + wx, centerY = d.Y + d.H / 2 + wy;
        (e.W, e.H) = (w, h);
        (e.X, e.Y) = (centerX - w / 2, centerY - h / 2);
    }

    /// <summary>可以在 3D 中拖曳高度的元素：有平面範圍、不是料架（層數 × 每層高度）、區域與文字。</summary>
    public static bool CanDragHeight(LayoutElementViewModel e) =>
        e.HasGeometry && e.Type is not (LayoutElementType.Rack or LayoutElementType.Area or LayoutElementType.Label);

    /// <summary>開始拖曳高度把手（ADR-0018 第 7 點）。</summary>
    public void BeginHeightDrag(LayoutElementViewModel element)
    {
        if (!element.IsSelected) SelectedElement = element;
        if (CanDragHeight(element)) _heightDrag = (element, element.Height);
    }

    /// <summary>把高度改為 height（公尺），吸附到 0.1 公尺，最小 0.1 公尺。放開（<see cref="EndDrag"/>）時算一步。</summary>
    public void DragHeightTo(double height)
    {
        if (_heightDrag is not { } d) return;
        d.Element.Height = Math.Round(Math.Max(HeightSnap, Math.Round(height / HeightSnap, MidpointRounding.AwayFromZero) * HeightSnap), 1);
    }

    /// <summary>未對應清單選取的編號（指派給選取的元素用）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AssignSelectedUnmappedCommand))]
    public partial UnmappedIdViewModel? SelectedUnmapped { get; set; }

    bool CanAssignSelectedUnmapped() => SelectedUnmapped is not null && SelectedElement is not null;

    /// <summary>
    /// 把未對應清單選取的編號設為選取元素的別名（#117 使用者決定：WebView2 收不到 WPF 的拖放，改為按鈕或雙擊編號）。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAssignSelectedUnmapped))]
    void AssignSelectedUnmapped()
    {
        if (SelectedUnmapped is not { } id) return;
        if (SelectedElement is not { } target)
        {
            Status = "先在 3D 畫布上選取一個元素，再指派編號。";
            return;
        }
        AssignUnmapped(id, target);
    }
}

/// <summary>3D 編輯器的把手：導軌的點（railPoint）與弧線中點（railSegment）、輸送線的交接點（from、to）。座標為樓層座標（公尺）。</summary>
public sealed record EditorHandle(string Kind, string Id, double X, double Y, int Index = 0, bool Selected = false);

/// <summary>
/// 3D 編輯器的狀態（ADR-0018）：目前樓層、是否只顯示目前樓層、選取的元素、單選時可以用的把手、存檔前檢查有問題的元素。
/// </summary>
public sealed record EditorSceneState(
    string CurrentFloor, bool OnlyCurrentFloor, IReadOnlyList<string> Selected, string? Primary,
    bool CanResize, bool CanHeight, bool CanRotate, IReadOnlyList<string> Issues, IReadOnlyList<EditorHandle> Handles);

public sealed partial class LayoutEditorViewModel
{
    /// <summary>目前的 Layout（含樓板高度），給 3D 畫布繪製；與存檔的內容相同。</summary>
    public Playback.SceneLayout BuildScene() => Playback.PlaybackScene.LayoutOf(new LayoutSection
    {
        Floors = FloorList.Select(f => new LayoutFloor(f, HeightOf(f))).ToList(),
        Elements = Elements.Select(e => e.ToModel()).ToList(),
    });

    /// <summary>3D 畫布的選取與把手。</summary>
    public EditorSceneState BuildSceneState()
    {
        var primary = SelectedElement;
        var handles = new List<EditorHandle>();
        if (primary is { IsRail: true })
            handles.AddRange(primary.RailHandles.Select(h => new EditorHandle(h.IsPoint ? "railPoint" : "railSegment", primary.Id, h.X, h.Y, h.Index, h.IsSelected)));
        if (primary is { IsConveyor: true })
        {
            if (primary.FromPoint is { } f) handles.Add(new EditorHandle("from", primary.Id, f.X, f.Y));
            if (primary.ToPoint is { } t) handles.Add(new EditorHandle("to", primary.Id, t.X, t.Y));
        }
        return new EditorSceneState(CurrentFloor, OnlyCurrentFloor, Elements.Where(e => e.IsSelected).Select(e => e.Id).ToList(), primary?.Id,
            CanResize: primary is { HasGeometry: true }, CanHeight: primary is not null && CanDragHeight(primary),
            CanRotate: primary is not null && (primary.HasGeometry || primary.IsRail),
            Elements.Where(e => e.HasIssue).Select(e => e.Id).ToList(), handles);
    }
}
