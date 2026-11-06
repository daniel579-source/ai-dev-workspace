using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogTrace.Engine.Parsing;
using LogTrace.Engine.Plants;
using LogTrace.Workspace.Layout;

namespace LogTrace.Workspace.Playback;

// 回放的樓層（ADR-0006 第 13 點）：單層／並排、橫向／垂直，以及「跟隨切換樓層」（預設關）。
public sealed partial class PlaybackViewModel
{
    Action<FloorViewSettings>? _saveView;
    bool _applyingView;

    public ObservableCollection<string> Floors { get; } = [LayoutFloors.Default];

    /// <summary>有兩層以上時才顯示樓層列。</summary>
    public bool HasFloors => Floors.Count > 1;

    [ObservableProperty]
    public partial string CurrentFloor { get; set; } = LayoutFloors.Default;

    [ObservableProperty]
    public partial bool SideBySide { get; set; }

    [ObservableProperty]
    public partial bool Vertical { get; set; }

    /// <summary>跟隨切換樓層：從 Layout、設備狀態表、命令或事件跳到某個元素時，單層檢視切到它的樓層。</summary>
    [ObservableProperty]
    public partial bool FollowFloor { get; set; }

    public ObservableCollection<FloorRegionViewModel> FloorRegions { get; } = [];

    /// <summary>套用本機的檢視設定；save 在使用者切換時呼叫。</summary>
    public void UseViewSettings(FloorViewSettings settings, Action<FloorViewSettings>? save)
    {
        _applyingView = true;
        try
        {
            (SideBySide, Vertical, FollowFloor) = (settings.SideBySide, settings.Vertical, settings.Follow);
        }
        finally
        {
            _applyingView = false;
        }
        _saveView = save;
        RefreshFloorView();
    }

    void ViewChanged()
    {
        if (_applyingView) return;
        _saveView?.Invoke(new FloorViewSettings(SideBySide, Vertical, FollowFloor));
        RefreshFloorView();
    }

    partial void OnSideBySideChanged(bool value) => ViewChanged();
    partial void OnVerticalChanged(bool value) => ViewChanged();
    partial void OnFollowFloorChanged(bool value) => ViewChanged();
    partial void OnCurrentFloorChanged(string value) => RefreshFloorView();

    void LoadFloors(LayoutSection layout)
    {
        Floors.Clear();
        foreach (var f in LayoutFloors.Of(layout)) Floors.Add(f);
        OnPropertyChanged(nameof(HasFloors));
        CurrentFloor = Floors[0];
    }

    bool SameFloor(string a, string b) => LayoutFloors.NameComparer.Equals(a, b);

    /// <summary>重算樓層區域、元素的位移與是否顯示，以及畫布大小（各層依該層的元素範圍計算）。</summary>
    void RefreshFloorView()
    {
        if (!Floors.Any(f => SameFloor(f, CurrentFloor))) CurrentFloor = Floors.FirstOrDefault() ?? LayoutFloors.Default;
        (string, double, double) Size(string floor)
        {
            var (w, h) = LayoutGeometry.CanvasSize(Elements.Where(e => SameFloor(e.Floor, floor)).Select(e => e.Model));
            return (floor, w, h);
        }
        var (_, width, height) = Size(CurrentFloor);
        IReadOnlyList<FloorRegion> regions = SideBySide
            ? LayoutFloors.Arrange(Floors.Select(Size).ToList(), Vertical)
            : [new FloorRegion(CurrentFloor, 0, 0, width, height)];
        foreach (var e in Elements)
        {
            var r = regions.FirstOrDefault(r => SameFloor(r.Floor, e.Floor));
            e.IsOnCanvas = r is not null;
            (e.OffsetX, e.OffsetY) = r is null ? (0, 0) : (r.OffsetX, r.OffsetY);
        }
        (CanvasWidth, CanvasHeight) = (regions.Max(r => r.OffsetX + r.Width), regions.Max(r => r.OffsetY + r.Height));
        var vms = regions.Select(r => new FloorRegionViewModel(r.Floor, r.OffsetX, r.OffsetY, r.Width, r.Height, SameFloor(r.Floor, CurrentFloor), SideBySide)).ToList();
        if (!vms.SequenceEqual(FloorRegions))
        {
            FloorRegions.Clear();
            foreach (var r in vms) FloorRegions.Add(r);
        }
    }

    /// <summary>點樓層頁籤。</summary>
    [RelayCommand]
    void SelectFloor(string? floor)
    {
        if (floor is not null) CurrentFloor = floor;
    }

    /// <summary>跟隨切換樓層開啟、單層檢視時，切到 floor。</summary>
    void Follow(string floor)
    {
        if (FollowFloor && !SideBySide && !SameFloor(floor, CurrentFloor)) CurrentFloor = floor;
    }

    /// <summary>設備狀態表選取的一列：選取 Layout 上的對應元素。</summary>
    [ObservableProperty]
    public partial EquipmentRowViewModel? SelectedEquipment { get; set; }

    partial void OnSelectedEquipmentChanged(EquipmentRowViewModel? value) => SelectElementById(value?.Id);

    /// <summary>點設備狀態表的一列：選取 Layout 上的元素（開啟跟隨時切換樓層）。</summary>
    public void SelectElementById(string? id)
    {
        if (id is not null && Elements.FirstOrDefault(e => e.Id == id) is { } el) SelectedElement = el;
    }

    /// <summary>
    /// 選取事件時：事件的欄位中第一個能以別名對應到 Layout 元素的編號，開啟跟隨時切換到該元素的樓層（不改變選取的元素）。
    /// </summary>
    public void FollowEvent(IReadOnlyList<EventField> fields)
    {
        if (!FollowFloor || SideBySide || _model is null) return;
        foreach (var f in fields.Where(f => FieldKinds.Identifiers.Contains(f.Kind)))
            if (_model.Resolve(f.Value) is { } target && Elements.FirstOrDefault(e => e.Id == target.Id) is { } el)
            {
                Follow(el.Floor);
                return;
            }
    }
}
