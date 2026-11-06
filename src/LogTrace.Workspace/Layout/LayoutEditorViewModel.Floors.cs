using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogTrace.Engine.Plants;

namespace LogTrace.Workspace.Layout;

/// <summary>樓層檢視的本機設定（ADR-0006 第 13 點）：單層／並排、橫向／垂直、回放的跟隨切換樓層。不寫進廠別設定檔。</summary>
public sealed record FloorViewSettings(bool SideBySide = false, bool Vertical = false, bool Follow = false);

/// <summary>畫布上一個樓層的區域（公尺）：並排檢視時畫出底色與名稱。</summary>
public sealed record FloorRegionViewModel(string Floor, double Left, double Top, double Width, double Height, bool IsCurrent, bool ShowLabel)
{
    /// <summary>名稱的位置（區域上方）。</summary>
    public double LabelTop => Top - LayoutFloors.LabelBand;
}

// 樓層（ADR-0006 第 13 點）：每個元素屬於一層；單層檢視只畫目前樓層，並排檢視把各層排在同一張畫布上（可以完整編輯）。
// 畫面上的座標（公尺）＝元素座標＋所在樓層區域的位移；滑鼠操作傳進來的都是畫面上的座標。
public sealed partial class LayoutEditorViewModel
{
    Action<FloorViewSettings>? _saveView;
    IReadOnlyList<FloorRegion> _regions = [];

    /// <summary>樓層名稱，順序即樓層順序。</summary>
    public ObservableCollection<string> Floors { get; } = [];

    IReadOnlyList<string> FloorList => Floors.Count > 0 ? Floors : [LayoutFloors.Default];

    /// <summary>目前樓層：單層檢視顯示的樓層；新增與（滑鼠不在畫布上時）貼上的元素放在這一層。</summary>
    [ObservableProperty]
    public partial string CurrentFloor { get; set; } = LayoutFloors.Default;

    /// <summary>並排檢視：所有樓層排在同一張畫布上。</summary>
    [ObservableProperty]
    public partial bool SideBySide { get; set; }

    /// <summary>並排時垂直排列（低樓層在下）；否則由左到右。</summary>
    [ObservableProperty]
    public partial bool Vertical { get; set; }

    /// <summary>畫布上的樓層區域（並排時每層一個）。</summary>
    public ObservableCollection<FloorRegionViewModel> FloorRegions { get; } = [];

    /// <summary>樓層列中輸入的名稱（改名用）；切換樓層時帶入目前樓層的名稱。</summary>
    [ObservableProperty]
    public partial string FloorNameText { get; set; } = LayoutFloors.Default;

    /// <summary>選取多個元素時，要移到的樓層。</summary>
    [ObservableProperty]
    public partial string? MoveTargetFloor { get; set; }

    public bool HasMultipleSelection => SelectionCount > 1;

    /// <summary>套用本機的檢視設定；save 在使用者切換時呼叫。</summary>
    public void UseViewSettings(FloorViewSettings settings, Action<FloorViewSettings>? save)
    {
        _saveView = null;
        (SideBySide, Vertical) = (settings.SideBySide, settings.Vertical);
        _saveView = save;
    }

    partial void OnCurrentFloorChanged(string value)
    {
        FloorNameText = value;
        RefreshLayoutView();
        // 單層檢視換到別層時，看不到的元素取消選取：避免被 Del 刪除或被拖曳一起移動（Jev，0.78）
        if (Elements.Any(e => e.IsSelected && !e.IsOnCanvas)) SetSelection(Selection.Where(e => e.IsOnCanvas));
    }

    partial void OnSideBySideChanged(bool value)
    {
        _saveView?.Invoke(new FloorViewSettings(SideBySide, Vertical));
        RefreshLayoutView();
    }

    partial void OnVerticalChanged(bool value)
    {
        _saveView?.Invoke(new FloorViewSettings(SideBySide, Vertical));
        RefreshLayoutView();
    }

    /// <summary>元素所在的樓層（清單中的寫法）：輸送線取起點（沒有時取終點）的樓層。</summary>
    public string FloorOf(LayoutElementViewModel e)
    {
        if (e.IsConveyor) return (Find(e.From) ?? Find(e.To)) is { } end ? FloorOf(end) : FloorList[0];
        return LayoutFloors.Canonical(e.Floor, FloorList) ?? FloorList[0];
    }

    bool SameFloor(string a, string b) => LayoutFloors.NameComparer.Equals(a, b);

    void LoadFloors(LayoutSection layout)
    {
        Floors.Clear();
        foreach (var f in LayoutFloors.Of(layout)) Floors.Add(f);
        CurrentFloor = Floors[0];
    }

    /// <summary>
    /// 重算樓層區域、每個元素的位移與是否顯示、輸送線兩端在畫面上的位置，以及畫布大小。
    /// 拖曳中不重算區域（區域大小會隨元素改變，避免畫面跳動），放開後再重算。
    /// </summary>
    void RefreshLayoutView()
    {
        if (!FloorList.Any(f => SameFloor(f, CurrentFloor))) CurrentFloor = FloorList[0];
        if (_drag is null && _pointDrag is null) _regions = ComputeRegions();
        foreach (var e in Elements)
        {
            var r = _regions.FirstOrDefault(r => SameFloor(r.Floor, FloorOf(e)));
            e.IsOnCanvas = r is not null;
            (e.OffsetX, e.OffsetY) = r is null ? (0, 0) : (r.OffsetX, r.OffsetY);
        }
        var byId = Elements.Where(e => e.HasGeometry).ToDictionary(e => e.Id);
        foreach (var c in Elements.Where(e => e.IsConveyor))
        {
            if (c.From is { } f && byId.TryGetValue(f, out var a))
                (c.LineX1, c.LineY1) = a.Type == LayoutElementType.Vehicle && c.FromPoint is { } p
                    ? (p.X + a.OffsetX, p.Y + a.OffsetY) : (a.CenterX + a.OffsetX, a.CenterY + a.OffsetY);
            if (c.To is { } t && byId.TryGetValue(t, out var b))
                (c.LineX2, c.LineY2) = b.Type == LayoutElementType.Vehicle && c.ToPoint is { } q
                    ? (q.X + b.OffsetX, q.Y + b.OffsetY) : (b.CenterX + b.OffsetX, b.CenterY + b.OffsetY);
        }
        (CanvasWidth, CanvasHeight) = _regions.Count == 0 ? (LayoutGeometry.MinCanvasWidth, LayoutGeometry.MinCanvasHeight)
            : (_regions.Max(r => r.OffsetX + r.Width), _regions.Max(r => r.OffsetY + r.Height));
        var regions = _regions.Select(r => new FloorRegionViewModel(r.Floor, r.OffsetX, r.OffsetY, r.Width, r.Height, SameFloor(r.Floor, CurrentFloor), SideBySide)).ToList();
        if (!regions.SequenceEqual(FloorRegions))
        {
            FloorRegions.Clear();
            foreach (var r in regions) FloorRegions.Add(r);
        }
    }

    /// <summary>單層：目前樓層一個區域；並排：每層一個，大小依該層元素範圍計算（ADR-0006 第 11、13 點）。</summary>
    IReadOnlyList<FloorRegion> ComputeRegions()
    {
        (double, double) Size(string floor) => LayoutGeometry.CanvasSize(Elements.Where(e => SameFloor(FloorOf(e), floor)).Select(e => e.ToModel()));
        if (!SideBySide)
        {
            var (w, h) = Size(CurrentFloor);
            return [new FloorRegion(CurrentFloor, 0, 0, w, h)];
        }
        return LayoutFloors.Arrange(FloorList.Select(f => { var (w, h) = Size(f); return (f, w, h); }).ToList(), Vertical);
    }

    /// <summary>畫面上的點 (x, y) 所在的樓層區域（落在空白處時取最近的）。</summary>
    FloorRegion RegionAt(double x, double y) => LayoutFloors.RegionAt(_regions, x, y) ?? new FloorRegion(CurrentFloor, 0, 0, 0, 0);

    /// <summary>並排時點了畫布上的空白處：目前樓層改為那一層。</summary>
    public void SetCurrentFloorAt(double x, double y)
    {
        if (SideBySide) CurrentFloor = RegionAt(x, y).Floor;
    }

    void FloorsChanged(string status)
    {
        IsDirty = true;
        RefreshLayoutView();
        Commit(null);
        Status = status;
    }

    /// <summary>點樓層頁籤。</summary>
    [RelayCommand]
    void SelectFloor(string? floor)
    {
        if (floor is not null) CurrentFloor = floor;
    }

    /// <summary>新增樓層：名稱為下一個沒用過的「nF」，加在最上面一層之後。</summary>
    [RelayCommand]
    void AddFloor()
    {
        if (Floors.Count == 0) Floors.Add(LayoutFloors.Default);
        int n = Floors.Count + 1;
        while (Floors.Any(f => SameFloor(f, $"{n}F"))) n++;
        Floors.Add($"{n}F");
        CurrentFloor = Floors[^1];
        FloorsChanged($"已新增樓層「{CurrentFloor}」。");
    }

    /// <summary>目前樓層改名：所有元素的樓層一併更新。名稱不可空白、不可重複（大小寫視為相同）。</summary>
    [RelayCommand]
    void RenameFloor()
    {
        var name = FloorNameText.Trim();
        var old = CurrentFloor;
        int i = Floors.IndexOf(old);
        if (i < 0 || name == old) return;
        if (name.Length == 0)
        {
            Status = "樓層名稱不可空白。";
            return;
        }
        if (Floors.Where((_, k) => k != i).Any(f => SameFloor(f, name)))
        {
            Status = $"已經有樓層「{name}」（大小寫視為相同）。";
            return;
        }
        Batch(() =>
        {
            foreach (var e in Elements.Where(e => !e.IsConveyor && SameFloor(FloorOf(e), old))) e.Floor = name;
            Floors[i] = name;
            CurrentFloor = name;
        });
        FloorsChanged($"樓層「{old}」已改名為「{name}」。");
    }

    [RelayCommand]
    void MoveFloorUp() => MoveFloor(+1);

    [RelayCommand]
    void MoveFloorDown() => MoveFloor(-1);

    /// <summary>上移＝往較高的樓層（清單中往後）。</summary>
    void MoveFloor(int by)
    {
        int i = Floors.IndexOf(CurrentFloor), j = i + by;
        if (i < 0 || j < 0 || j >= Floors.Count) return;
        Floors.Move(i, j);
        FloorsChanged($"已調整樓層順序：{string.Join("、", Floors)}。");
    }

    /// <summary>刪除目前樓層：還有元素時擋下；至少保留一層。</summary>
    [RelayCommand]
    void DeleteFloor()
    {
        int i = Floors.IndexOf(CurrentFloor);
        if (i < 0) return;
        if (Floors.Count == 1)
        {
            Status = "至少要有一個樓層。";
            return;
        }
        int count = Elements.Count(e => !e.IsConveyor && SameFloor(FloorOf(e), CurrentFloor));
        if (count > 0)
        {
            Status = $"樓層「{CurrentFloor}」還有 {count} 個元素，請先把它們移到其他樓層或刪除。";
            return;
        }
        var name = CurrentFloor;
        Floors.RemoveAt(i);
        CurrentFloor = Floors[Math.Max(0, i - 1)];
        FloorsChanged($"已刪除樓層「{name}」。");
    }

    /// <summary>把選取的元素移到 MoveTargetFloor（座標不變）。</summary>
    [RelayCommand]
    void MoveSelectionToFloor()
    {
        if (MoveTargetFloor is not { } target) return;
        var items = Selection.Where(e => e.HasGeometry).ToList();
        Batch(() =>
        {
            foreach (var e in items) e.Floor = target;
        });
        Status = $"已把 {items.Count} 個元素移到「{target}」。";
    }
}
