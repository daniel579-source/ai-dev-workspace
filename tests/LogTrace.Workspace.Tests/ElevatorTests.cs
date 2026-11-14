using LogTrace.Engine.Parsing;
using LogTrace.Engine.Playback;
using LogTrace.Engine.Plants;
using LogTrace.Tests.Support;
using LogTrace.Workspace.Layout;
using LogTrace.Workspace.Playback;

namespace LogTrace.Workspace.Tests;

// 電梯的編輯與回放（ADR-0005 第 11 點、ADR-0006 第 15 點，#102）。內容全部是虛構的。
public sealed class ElevatorEditorTests : IDisposable
{
    readonly TempFolder _tmp = new();
    readonly PlantConfigLibrary _library;
    readonly LayoutEditorViewModel _vm;

    public ElevatorEditorTests()
    {
        _library = new PlantConfigLibrary(Path.Combine(_tmp.Path, "Plants"));
        _vm = new LayoutEditorViewModel(_library, new FakeUserInteraction());
        _vm.AddFloorCommand.Execute(null); // 1F、2F
        _vm.AddFloorCommand.Execute(null); // 3F
        _vm.SelectFloorCommand.Execute("1F");
    }

    public void Dispose() => _tmp.Dispose();

    LayoutElementViewModel AddElevator()
    {
        _vm.AddElementCommand.Execute(LayoutElementType.Elevator);
        return _vm.SelectedElement!;
    }

    [Fact]
    public void 新增電梯預設服務所有樓層_在每一層都畫出來_勾選框修改服務的樓層()
    {
        var ev = AddElevator();

        Assert.Equal(("ev-1", "電梯"), (ev.Id, ev.TypeLabel));
        Assert.True(ev.CanHaveAliases);
        Assert.Equal(["1F", "2F", "3F"], ev.ServedFloors);
        Assert.Equal(3, _vm.ServedFloorChoices.Count);
        foreach (var f in new[] { "1F", "2F", "3F" })
        {
            _vm.SelectFloorCommand.Execute(f);
            Assert.True(ev.IsOnCanvas);
        }

        _vm.SelectedElement = ev;
        _vm.ServedFloorChoices.Single(c => c.Floor == "3F").IsChecked = false;
        Assert.Equal(["1F", "2F"], ev.ServedFloors);
        _vm.SelectFloorCommand.Execute("3F");
        Assert.False(ev.IsOnCanvas);
    }

    [Fact]
    public void 並排時其他服務樓層畫分身_拖曳分身移動電梯_拖到別層不換樓層()
    {
        var ev = AddElevator();
        (ev.X, ev.Y) = (4, 4);
        _vm.SideBySide = true;

        Assert.Equal(2, _vm.ElevatorCopies.Count); // 2F、3F 的分身
        var copy = _vm.ElevatorCopies.Single(c => c.Floor == "2F");
        Assert.Equal(4 + copy.OffsetX, copy.ViewX);

        _vm.BeginDrag(ev);
        _vm.DragMoveBy(200, 1); // 拖得很遠（落在別層的區域）
        _vm.EndDrag();
        Assert.Equal(["1F", "2F", "3F"], ev.ServedFloors);
        Assert.Equal(204, ev.X);
        var after = _vm.ElevatorCopies.Single(c => c.Floor == "2F"); // 1F 的區域變寬，2F 的位移跟著改，分身重建
        Assert.Equal(ev.X + after.OffsetX, after.ViewX); // 分身跟著電梯移動
    }

    [Fact]
    public void 樓層改名一併更新_刪除服務的樓層時擋下_存檔讀回_輸送線不能接電梯()
    {
        var ev = AddElevator();
        _vm.SelectFloorCommand.Execute("2F");
        _vm.FloorNameText = "夾層";
        _vm.RenameFloorCommand.Execute(null);
        Assert.Equal(["1F", "夾層", "3F"], ev.ServedFloors);

        _vm.DeleteFloorCommand.Execute(null);
        Assert.Contains("請先從電梯的服務樓層移除", _vm.Status);
        Assert.Equal(3, _vm.Floors.Count);

        _vm.SaveCommand.Execute(null);
        var saved = PlantConfigFile.Load(_library.List().Single());
        Assert.Equal(5, saved.FormatVersion);
        Assert.Equal(["1F", "夾層", "3F"], saved.Layout.Elements.Single(e => e.Id == ev.Id).ServedFloors);
        Assert.Null(saved.Layout.Elements.Single(e => e.Id == ev.Id).Floor);
        Assert.DoesNotContain(_vm.ConveyorEndpoints, e => e.IsElevator);
    }
}

public sealed class ElevatorPlaybackTests
{
    static PlaybackViewModel Load(FloorViewSettings view)
    {
        var layout = new LayoutSection
        {
            Floors = ["1F", "2F"],
            Elements =
            [
                new() { Id = "st-1", Type = LayoutElementType.Station, Name = "1F 出入口", Aliases = ["A-01"], Floor = "1F", X = 1, Y = 1, W = 2, H = 2 },
                new() { Id = "st-2", Type = LayoutElementType.Station, Name = "2F 出入口", Aliases = ["B-01"], Floor = "2F", X = 1, Y = 1, W = 2, H = 2 },
                new() { Id = "ev-1", Type = LayoutElementType.Elevator, Name = "電梯 1", Aliases = ["L1"], ServedFloors = ["1F", "2F"], X = 4, Y = 1, W = 2, H = 2 },
            ],
        };
        static EventEffect E(long seq, string effect, params (string Kind, string Value)[] p) =>
            new(seq, effect, p.ToDictionary(x => x.Kind, x => (IReadOnlyList<string>)[x.Value]));
        var effects = new List<EventEffect>
        {
            E(1, EffectKinds.CommandCreated, (FieldKinds.Command, "C-1"), (FieldKinds.Pallet, "P-001"), (FieldKinds.Source, "A-01")),
            E(2, EffectKinds.ElevatorDispatched, (FieldKinds.Command, "C-1"), (FieldKinds.Elevator, "L1"), (FieldKinds.Destination, "B-01"), (FieldKinds.Source, "A-01")),
            E(3, EffectKinds.ElevatorArrived, (FieldKinds.Command, "C-1"), (FieldKinds.Destination, "B-01")),
        };
        var p = new PlaybackViewModel();
        p.UseViewSettings(view, null);
        p.Load(new WorldModel(effects, layout, "在席"), layout, [], hasStateRules: true);
        return p;
    }

    [Fact]
    public void 移動中顯示出發與前往樓層並標為推估_貨物在電梯上_抵達後依LOG_設備狀態表列出目前樓層()
    {
        var p = Load(new FloorViewSettings());
        var ev = p.Elements.Single(e => e.Id == "ev-1");

        p.SetMoment(1);
        Assert.Equal(ElementLook.Unknown, ev.Look);
        Assert.Equal("—", p.Equipment.Single(r => r.Id == "ev-1").Floor);

        p.SetMoment(2);
        Assert.Contains("移動中：1F → 2F", ev.Summary);
        Assert.Contains("P-001", ev.Summary); // 載著的棧板
        Assert.True(ev.IsEstimated);
        Assert.Equal(ElementLook.Executing, ev.Look);
        p.SelectElementCommand.Execute(ev);
        Assert.Contains("移動中，推估", p.Detail.Single(d => d.Label == "目前樓層").Value);
        Assert.Contains("P-001", p.Detail.Single(d => d.Label == "載著").Value);

        p.SetMoment(3);
        Assert.False(ev.IsEstimated);
        Assert.StartsWith("2F（依 LOG", p.Detail.Single(d => d.Label == "目前樓層").Value);
        Assert.Equal(3L, p.Detail.Single(d => d.Label == "目前樓層").Cause);
        Assert.Equal("2F", p.Equipment.Single(r => r.Id == "ev-1").Floor);
    }

    [Fact]
    public void 電梯在3D場景中跨越服務的樓層_狀態帶出目前樓層()
    {
        var p = Load(new FloorViewSettings());

        var ev = p.Scene.Elements.Single(e => e.Id == "ev-1");
        Assert.Equal(["1F", "2F"], ev.ServedFloors);
        Assert.Null(p.SceneNow.Elements.Single(e => e.Id == "ev-1").ElevatorFloor); // 未取得資訊：不畫台車

        p.SetMoment(3); // 抵達 2F
        Assert.Equal("2F", p.SceneNow.Elements.Single(e => e.Id == "ev-1").ElevatorFloor);
    }
}
