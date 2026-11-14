using LogTrace.Engine.Plants;
using LogTrace.Tests.Support;
using LogTrace.Workspace.Layout;

namespace LogTrace.Workspace.Tests;

// Layout 編輯器的 3D 操作（ADR-0018，#117）：網頁回報目前樓層平面上的座標，ViewModel 套用吸附與復原。內容全部是虛構的。
public sealed class LayoutEditor3DTests : IDisposable
{
    readonly TempFolder _tmp = new();
    readonly LayoutEditorViewModel _vm;

    public LayoutEditor3DTests()
    {
        _vm = new LayoutEditorViewModel(new PlantConfigLibrary(Path.Combine(_tmp.Path, "Plants")), new FakeUserInteraction());
    }

    public void Dispose() => _tmp.Dispose();

    LayoutElementViewModel Station(double x, double y, double w = 2, double h = 1, int rotation = 0)
    {
        _vm.AddElementCommand.Execute(LayoutElementType.Station);
        var e = _vm.SelectedElement!;
        (e.X, e.Y, e.W, e.H, e.Rotation) = (x, y, w, h, rotation);
        return e;
    }

    [Theory]
    [InlineData(1, 1, 1.0, 1.0, 10.0, 10.0, 3.0, 2.0)]     // 右下角往右下拖：左上角不動
    [InlineData(-1, -1, -1.0, -1.0, 9.0, 9.0, 3.0, 2.0)]   // 左上角往左上拖：右下角不動
    [InlineData(1, -1, 1.0, 1.0, 10.0, 10.5, 3.0, 0.5)]    // 右上角往下拖：深變小（最小一格），下緣不動
    public void 四角調整大小_拖哪一角就移動那一角_對角不動(int sx, int sy, double dx, double dy, double x, double y, double w, double h)
    {
        var e = Station(10, 10);

        _vm.BeginDrag(e);
        _vm.DragResizeCornerBy(sx, sy, dx, dy);

        Assert.Equal((x, y, w, h), (e.X, e.Y, e.W, e.H));
    }

    [Fact]
    public void 旋轉90度的元素_調整大小以元素本身的方向計算_中心跟著移動()
    {
        var e = Station(10, 10, w: 2, h: 1, rotation: 90); // 畫面上：寬 1（x 方向）、深 2（y 方向），中心 (11, 10.5)

        _vm.BeginDrag(e);
        _vm.DragResizeCornerBy(1, 1, 0, 1); // 往畫面下方拖 1 公尺：元素本身的寬（W）變大
        Assert.Equal((3d, 1d), (e.W, e.H));
        Assert.Equal((11d, 11d), (e.X + e.W / 2, e.Y + e.H / 2)); // 中心往下移半公尺
    }

    [Fact]
    public void 拖曳高度_吸附到0點1公尺_最小0點1公尺_整個拖曳算一步()
    {
        var e = Station(1, 1);
        var before = e.Height;
        var undo = _vm.UndoCount;

        _vm.BeginHeightDrag(e);
        _vm.DragHeightTo(1.234);
        Assert.Equal(1.2, e.Height);
        _vm.DragHeightTo(-3);
        Assert.Equal(0.1, e.Height);
        _vm.DragHeightTo(2.06);
        _vm.EndDrag();
        Assert.Equal(2.1, e.Height);
        Assert.Equal(undo + 1, _vm.UndoCount);

        _vm.UndoCommand.Execute(null);
        Assert.Equal(before, _vm.Elements.Single().Height);
    }

    [Fact]
    public void 料架區域文字不能拖曳高度()
    {
        _vm.AddElementCommand.Execute(LayoutElementType.Rack);
        var rack = _vm.SelectedElement!;
        var height = rack.Height;

        _vm.BeginHeightDrag(rack);
        _vm.DragHeightTo(9);
        _vm.EndDrag();

        Assert.Equal(height, rack.Height);
        Assert.False(_vm.BuildSceneState().CanHeight);
        Assert.True(_vm.BuildSceneState().CanResize);
    }

    [Fact]
    public void 未對應的編號_選元素後指派為別名()
    {
        _vm.AddElementCommand.Execute(LayoutElementType.Vehicle);
        var vehicle = _vm.SelectedElement!;
        var id = new UnmappedIdViewModel(Engine.Parsing.FieldKinds.Vehicle, "SHTL01", 3);

        _vm.SelectedUnmapped = id;
        Assert.True(_vm.AssignSelectedUnmappedCommand.CanExecute(null));
        _vm.AssignSelectedUnmappedCommand.Execute(null);
        Assert.Contains("SHTL01", vehicle.Aliases);

        _vm.SelectedElement = null;
        Assert.False(_vm.AssignSelectedUnmappedCommand.CanExecute(null)); // 沒有選取元素時不能指派
    }

    [Fact]
    public void 場景與狀態_選取的元素_把手_有問題的元素()
    {
        var a = Station(1, 1);
        var b = Station(5, 1);
        _vm.AddElementCommand.Execute(LayoutElementType.Rail);
        var rail = _vm.SelectedElement!;

        var state = _vm.BuildSceneState();
        Assert.Equal(rail.Id, state.Primary);
        Assert.Contains(state.Handles, h => h.Kind == "railPoint" && h.Index == 0);
        Assert.Contains(state.Handles, h => h.Kind == "railSegment");
        Assert.False(state.CanResize);
        Assert.True(state.CanRotate);

        _vm.SelectInRect(0, 0, 10, 3, add: false);
        state = _vm.BuildSceneState();
        Assert.Contains(a.Id, state.Selected);
        Assert.Contains(b.Id, state.Selected);
        Assert.Null(state.Primary); // 多選時沒有把手

        a.Aliases.Add("A-01");
        b.Aliases.Add("A-01");
        _vm.PlantName = "範例廠";
        _vm.SaveCommand.Execute(null); // 別名重複：擋下並標出
        Assert.Equal([a.Id, b.Id], _vm.BuildSceneState().Issues.Order());

        var scene = _vm.BuildScene();
        Assert.Equal(["station", "station", "rail"], scene.Elements.Select(e => e.Type));
        Assert.Equal("1F", scene.Floors.Single().Name);
    }

    [Fact]
    public void 樓板高度與只顯示目前樓層_反映在場景與狀態()
    {
        _vm.AddFloorCommand.Execute(null);
        _vm.FloorHeightText = "6";
        _vm.SetFloorHeightCommand.Execute(null);
        _vm.OnlyCurrentFloor = true;

        Assert.Equal([("1F", 0d), ("2F", 5d)], _vm.BuildScene().Floors.Select(f => (f.Name, f.Elevation)));
        Assert.Equal(6, _vm.BuildScene().Floors[1].Height);
        Assert.Equal(("2F", true), (_vm.BuildSceneState().CurrentFloor, _vm.BuildSceneState().OnlyCurrentFloor));
    }
}
