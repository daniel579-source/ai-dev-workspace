using LogTrace.Engine.Playback;
using LogTrace.Engine.Plants;
using LogTrace.Tests.Support;
using LogTrace.Workspace.Layout;
using LogTrace.Workspace.Playback;

namespace LogTrace.Workspace.Tests;

// 導軌的編輯與回放（ADR-0006 第 14 點，#100）。內容全部是虛構的。
public sealed class RailEditorTests : IDisposable
{
    readonly TempFolder _tmp = new();
    readonly PlantConfigLibrary _library;
    readonly LayoutEditorViewModel _vm;

    public RailEditorTests()
    {
        _library = new PlantConfigLibrary(Path.Combine(_tmp.Path, "Plants"));
        _vm = new LayoutEditorViewModel(_library, new FakeUserInteraction());
    }

    public void Dispose() => _tmp.Dispose();

    LayoutElementViewModel AddRail(bool loop = false)
    {
        if (loop) _vm.AddRailLoopCommand.Execute(null);
        else _vm.AddElementCommand.Execute(LayoutElementType.Rail);
        return _vm.SelectedElement!;
    }

    RailHandleViewModel Handle(LayoutElementViewModel rail, bool point, int index) => rail.RailHandles.Single(h => h.IsPoint == point && h.Index == index);

    [Fact]
    public void 範本_直線20公尺_跑道形環閉合_建立後就是一般的路徑()
    {
        var line = AddRail();
        Assert.Equal("rl-1", line.Id);
        Assert.Equal("導軌", line.TypeLabel);
        Assert.False(line.CanHaveAliases); // 輔助元素
        Assert.Equal(20, line.Path().Length, 6);
        Assert.Equal(3, line.RailHandles.Count); // 2 個點＋1 段的中點
        var loop = AddRail(loop: true);
        Assert.True(loop.Closed);
        Assert.Equal(40 + Math.PI * 6, loop.Path().Length, 6);
        Assert.Equal(8, loop.RailHandles.Count); // 4 個點＋4 個中點
        Assert.Equal(4, _vm.RailSegments.Count);
    }

    [Fact]
    public void 拖曳點吸附0點5公尺_拖曳中點變弧線_最多半圓_一次拖曳算一步()
    {
        var rail = AddRail(); // (1, y) → (21, y)
        var y = rail.RailPoints[0].Y;

        _vm.BeginRailHandleDrag(Handle(rail, point: true, 1));
        _vm.DragRailHandleTo(18.3, y + 2.2);
        _vm.EndDrag();
        Assert.Equal((18.5, y + 2), (rail.RailPoints[1].X, rail.RailPoints[1].Y));
        Assert.Equal(1, _vm.SelectedRailPoint);

        _vm.UndoCommand.Execute(null);
        rail = _vm.Elements.Single();
        Assert.Equal(21, rail.RailPoints[1].X);

        _vm.SelectedElement = rail;
        _vm.BeginRailHandleDrag(Handle(rail, point: false, 0));
        _vm.DragRailHandleTo(11, y - 4.2); // 往上（行進方向的左邊）4 公尺：凸度 2 × 4 / 20 = 0.4
        Assert.Equal(0.4, rail.RailPoints[0].Bulge, 6);
        _vm.DragRailHandleTo(11, y + 50); // 往下很遠：最多半圓
        Assert.Equal(-1, rail.RailPoints[0].Bulge, 6);
        _vm.EndDrag();
        Assert.Equal(0, _vm.SelectedRailSegment);
    }

    [Fact]
    public void 插入點_選一段按插入點_或雙擊_刪除點至少保留2點_這段改直線()
    {
        var rail = AddRail();
        var y = rail.RailPoints[0].Y;

        _vm.SelectedRailSegment = 0;
        _vm.InsertRailPointCommand.Execute(null);
        Assert.Equal(3, rail.RailPoints.Count);
        Assert.Equal(11, rail.RailPoints[1].X);
        Assert.Equal(1, _vm.SelectedRailPoint);

        _vm.InsertRailPointAt(rail, 5.2, y + 3); // 投影到 x = 5.2
        Assert.Equal(4, rail.RailPoints.Count);
        Assert.Equal(5.2, rail.RailPoints[1].X, 3);

        _vm.DeleteRailPointCommand.Execute(null);
        _vm.SelectedRailPoint = 1;
        _vm.DeleteRailPointCommand.Execute(null);
        Assert.Equal(2, rail.RailPoints.Count);
        _vm.SelectedRailPoint = 0;
        _vm.DeleteRailPointCommand.Execute(null);
        Assert.Contains("至少要有 2 個點", _vm.Status);

        rail.RailPoints = [rail.RailPoints[0] with { B = 0.5 }, rail.RailPoints[1]];
        _vm.SelectedRailSegment = 0;
        _vm.StraightenRailSegmentCommand.Execute(null);
        Assert.Null(rail.RailPoints[0].B);
    }

    [Fact]
    public void 整條拖曳移動_轉90度_框選碰到線就算()
    {
        var rail = AddRail();
        var before = rail.RailPoints;

        _vm.BeginDrag(rail);
        _vm.DragMoveBy(3.2, 1.9);
        _vm.EndDrag();
        Assert.Equal(before.Select(p => (p.X + 3, p.Y + 2)), rail.RailPoints.Select(p => (p.X, p.Y)));

        _vm.RotateClockwiseCommand.Execute(null);
        var (l, t, r, b) = rail.Path().Bounds();
        Assert.Equal((0d, 20d), (r - l, b - t)); // 變成直的
        Assert.True(l >= 0 && t >= 0);

        _vm.SelectedElement = null;
        _vm.SelectInRect(l - 0.2, (t + b) / 2, l + 0.2, (t + b) / 2 + 0.2, add: false);
        Assert.Equal([rail], _vm.Selection);
    }

    [Fact]
    public void 搬送機指定同一層的導軌_刪除導軌時清除指定_復原後回來_存檔讀回()
    {
        var rail = AddRail();
        _vm.AddElementCommand.Execute(LayoutElementType.Vehicle);
        var vehicle = _vm.SelectedElement!;
        Assert.Equal([rail], _vm.RailChoices);
        vehicle.Rail = rail.Id;

        _vm.SaveCommand.Execute(null);
        var saved = PlantConfigFile.Load(_library.List().Single());
        Assert.Equal(4, saved.FormatVersion);
        Assert.Equal(rail.Id, saved.Layout.Elements.Single(e => e.Id == vehicle.Id).Rail);
        Assert.Equal(rail.RailPoints, saved.Layout.Elements.Single(e => e.Id == rail.Id).Points);

        _vm.SelectedElement = _vm.Elements.Single(e => e.Id == rail.Id);
        _vm.DeleteSelectedCommand.Execute(null);
        Assert.Null(_vm.Elements.Single(e => e.Id == vehicle.Id).Rail);
        Assert.Contains("1 台搬送機的導軌指定已清除", _vm.Status);
        _vm.UndoCommand.Execute(null);
        Assert.Equal(rail.Id, _vm.Elements.Single(e => e.Id == vehicle.Id).Rail);
    }

    [Fact]
    public void 搬送機的導軌選單只列同一層_貼上的搬送機清除導軌指定_導軌跟著平移()
    {
        var rail = AddRail();
        _vm.AddElementCommand.Execute(LayoutElementType.Vehicle);
        var vehicle = _vm.SelectedElement!;
        vehicle.Rail = rail.Id;
        _vm.AddFloorCommand.Execute(null);
        var other = AddRail(); // 2F
        _vm.SelectedElement = vehicle;
        Assert.Equal([rail], _vm.RailChoices);

        _vm.SelectInRect(0, 0, 60, 60, add: false); // 1F 的導軌與搬送機
        _vm.CopyCommand.Execute(null);
        _vm.Paste(null, null);
        var pastedVehicle = _vm.Selection.Single(e => e.IsVehicle);
        var pastedRail = _vm.Selection.Single(e => e.IsRail);
        Assert.Null(pastedVehicle.Rail);
        Assert.Equal(rail.RailPoints[0].X + 1, pastedRail.RailPoints[0].X);
        Assert.NotEqual(other.Id, pastedRail.Id);
    }

    [Fact]
    public void 拖曳整條導軌不換樓層_不超出左上角()
    {
        var rail = AddRail(); // 1F，(1, 1) → (21, 1)
        _vm.AddFloorCommand.Execute(null);
        _vm.SelectFloorCommand.Execute("1F");

        _vm.BeginDrag(rail);
        _vm.DragMoveBy(-10, 3);
        _vm.EndDrag();

        Assert.Equal("1F", rail.Floor);
        Assert.Equal((0d, 4d), (rail.RailPoints[0].X, rail.RailPoints[0].Y));
    }
}

public sealed class RailPlaybackTests
{
    [Fact]
    public void 回放畫出導軌_屬於輔助元素()
    {
        var layout = new LayoutSection
        {
            Floors = ["1F", "2F"],
            Elements =
            [
                new() { Id = "rl-1", Type = LayoutElementType.Rail, Name = "導軌 1", Floor = "2F", Points = RailPath.LoopTemplate(4, 2), Closed = true },
                new() { Id = "st-1", Type = LayoutElementType.Station, Name = "站點 1", Floor = "1F", X = 1, Y = 1, W = 2, H = 2 },
            ],
        };
        var p = new PlaybackViewModel();
        p.UseViewSettings(new FloorViewSettings(), null);
        p.Load(new WorldModel([], layout, "在席"), layout, [], hasStateRules: true);

        var rail = p.Elements.Single(e => e.Id == "rl-1");
        Assert.True(rail.IsRail && rail.IsAuxiliary && !rail.HasGeometry);
        Assert.Equal(ElementLook.Auxiliary, rail.Look);
        var scene = p.Scene.Elements.Single(e => e.Id == "rl-1");
        Assert.Equal(("rail", "2F", true), (scene.Type, scene.Floor, scene.Closed));
        Assert.Equal(RailPath.Of(layout.Elements[0]).Polyline().Count, scene.Path!.Count); // 3D 畫導軌的折線
        Assert.DoesNotContain(p.Equipment, r => r.Id == "rl-1");
    }
}
