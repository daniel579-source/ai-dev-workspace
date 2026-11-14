using LogTrace.Engine.Parsing;
using LogTrace.Engine.Playback;
using LogTrace.Engine.Plants;
using LogTrace.Tests.Support;
using LogTrace.Workspace.Layout;
using LogTrace.Workspace.Playback;

namespace LogTrace.Workspace.Tests;

// 樓層的編輯（ADR-0006 第 13 點，#99）。內容全部是虛構的。
public sealed class FloorEditorTests : IDisposable
{
    readonly TempFolder _tmp = new();
    readonly PlantConfigLibrary _library;
    readonly List<FloorViewSettings> _saved = [];
    readonly LayoutEditorViewModel _vm;

    public FloorEditorTests()
    {
        _library = new PlantConfigLibrary(Path.Combine(_tmp.Path, "Plants"));
        _vm = new LayoutEditorViewModel(_library, new FakeUserInteraction(), null, new FloorViewSettings(), _saved.Add);
    }

    public void Dispose() => _tmp.Dispose();

    LayoutElementViewModel Add(LayoutElementType type, double x = 1, double y = 1)
    {
        _vm.AddElementCommand.Execute(type);
        var e = _vm.SelectedElement!;
        (e.X, e.Y) = (x, y);
        return e;
    }

    [Fact]
    public void 新設定只有1F_新增樓層後新元素放在目前樓層_單層檢視只畫目前樓層()
    {
        Assert.Equal(["1F"], _vm.Floors);
        var a = Add(LayoutElementType.Station);
        _vm.AddFloorCommand.Execute(null);

        Assert.Equal(["1F", "2F"], _vm.Floors);
        Assert.Equal("2F", _vm.CurrentFloor);
        var b = Add(LayoutElementType.Station);
        Assert.Equal(("1F", "2F"), (a.Floor, b.Floor));
        Assert.False(a.IsOnCanvas);
        Assert.True(b.IsOnCanvas);

        _vm.SelectFloorCommand.Execute("1F");
        Assert.True(a.IsOnCanvas);
        Assert.False(b.IsOnCanvas);
        Assert.False(b.IsSelected); // 看不到的元素取消選取
        _vm.SelectedElement = b; // 從元素清單選了別層的元素：切到那一層
        Assert.Equal("2F", _vm.CurrentFloor);
    }

    [Fact]
    public void 樓層改名時元素一併更新_名稱不可空白或重複_復原後回來()
    {
        Add(LayoutElementType.Station);
        _vm.AddFloorCommand.Execute(null);
        var b = Add(LayoutElementType.Station);

        _vm.FloorNameText = "1f";
        _vm.RenameFloorCommand.Execute(null);
        Assert.Contains("已經有樓層", _vm.Status); // 大小寫視為相同
        _vm.FloorNameText = " ";
        _vm.RenameFloorCommand.Execute(null);
        Assert.Contains("不可空白", _vm.Status);

        _vm.FloorNameText = "夾層";
        _vm.RenameFloorCommand.Execute(null);
        Assert.Equal(["1F", "夾層"], _vm.Floors);
        Assert.Equal("夾層", b.Floor);

        _vm.UndoCommand.Execute(null);
        Assert.Equal(["1F", "2F"], _vm.Floors);
        Assert.Equal("2F", _vm.Elements.Single(e => e.Id == b.Id).Floor);
    }

    [Fact]
    public void 刪除還有元素的樓層時擋下_至少保留一層_調整順序()
    {
        Add(LayoutElementType.Station);
        _vm.DeleteFloorCommand.Execute(null);
        Assert.Contains("至少要有一個樓層", _vm.Status);

        _vm.AddFloorCommand.Execute(null);
        var b = Add(LayoutElementType.Station);
        _vm.DeleteFloorCommand.Execute(null);
        Assert.Contains("還有 1 個元素", _vm.Status);
        Assert.Equal(2, _vm.Floors.Count);

        _vm.MoveFloorDownCommand.Execute(null);
        Assert.Equal(["2F", "1F"], _vm.Floors);
        b.Floor = "1F";
        _vm.SelectFloorCommand.Execute("2F");
        _vm.DeleteFloorCommand.Execute(null);
        Assert.Equal(["1F"], _vm.Floors);
    }

    [Fact]
    public void 只顯示目前樓層_預設關_切換時記住設定_框選只選目前樓層()
    {
        var a = Add(LayoutElementType.Station);
        _vm.AddFloorCommand.Execute(null);
        var b = Add(LayoutElementType.Station); // 2F，座標與 a 相同

        Assert.False(_vm.OnlyCurrentFloor);
        _vm.OnlyCurrentFloor = true;
        Assert.Equal(new FloorViewSettings(OnlyCurrentFloor: true), _saved[^1]);
        Assert.True(_vm.BuildSceneState().OnlyCurrentFloor);

        _vm.SelectInRect(0, 0, 10, 10, add: false); // 目前樓層 2F
        Assert.Equal([b], _vm.Selection);
        _vm.SelectFloorCommand.Execute("1F");
        Assert.Empty(_vm.Selection); // 換層時別層的元素取消選取
        _vm.SelectInRect(0, 0, 10, 10, add: false);
        Assert.Equal([a], _vm.Selection);
    }

    [Fact]
    public void 拖曳不換樓層_座標不小於0_整個拖曳算一步()
    {
        var a = Add(LayoutElementType.Station, 2, 2);
        _vm.AddFloorCommand.Execute(null);
        _vm.SelectFloorCommand.Execute("1F");

        _vm.BeginDrag(a);
        _vm.DragMoveBy(100, -10);
        _vm.EndDrag();

        Assert.Equal("1F", a.Floor);
        Assert.Equal((102d, 0d), (a.X, a.Y));
        _vm.UndoCommand.Execute(null);
        Assert.Equal((2d, 2d), (_vm.Elements.Single().X, _vm.Elements.Single().Y));
    }

    [Fact]
    public void 貼上到目前樓層的滑鼠位置_多選一起移到其他樓層()
    {
        var a = Add(LayoutElementType.Station, 2, 2);
        var b = Add(LayoutElementType.Station, 6, 2);
        _vm.AddFloorCommand.Execute(null);

        _vm.SelectFloorCommand.Execute("1F");
        _vm.SelectInRect(0, 0, 10, 5, add: false);
        _vm.CopyCommand.Execute(null);
        _vm.SelectFloorCommand.Execute("2F");
        _vm.Paste(4, 4);
        Assert.All(_vm.Selection, e => Assert.Equal("2F", e.Floor));
        Assert.Equal(4, _vm.Selection.Min(e => e.X));

        _vm.SelectFloorCommand.Execute("1F");
        _vm.SelectInRect(0, 0, 10, 5, add: false); // 1F 的兩個
        Assert.True(_vm.HasMultipleSelection);
        _vm.MoveTargetFloor = "2F";
        _vm.MoveSelectionToFloorCommand.Execute(null);
        Assert.Equal(("2F", "2F"), (a.Floor, b.Floor));
    }

    [Fact]
    public void 存檔讀回樓層與元素的樓層_輸送線兩端不同層時存檔擋下()
    {
        var a = Add(LayoutElementType.Station, 2, 2);
        var b = Add(LayoutElementType.Station, 8, 2);
        _vm.AddElementCommand.Execute(LayoutElementType.Conveyor);
        _vm.AddFloorCommand.Execute(null);
        b.Floor = "2F";

        _vm.SaveCommand.Execute(null);
        Assert.Contains("兩端不在同一層", _vm.Status);

        b.Floor = "1F";
        _vm.SaveCommand.Execute(null);
        var layout = PlantConfigFile.Load(_library.List().Single()).Layout;
        Assert.Equal(["1F", "2F"], layout.Floors);
        Assert.Equal("1F", layout.Elements.Single(e => e.Id == a.Id).Floor);
    }

    [Fact]
    public void 防火門的依位置帶入只看同一層的輸送線()
    {
        Add(LayoutElementType.Station, 1, 1);
        Add(LayoutElementType.Station, 10, 1);
        _vm.AddElementCommand.Execute(LayoutElementType.Conveyor); // 1F，y = 2
        _vm.AddFloorCommand.Execute(null);
        var door = Add(LayoutElementType.FireDoor, 6, 1.75); // 2F，位置剛好在 1F 的線上

        _vm.SuggestDoorConveyorsCommand.Execute(null);

        Assert.Empty(door.Conveyors);
    }
}

// 樓層的回放（ADR-0006 第 13 點，#99）。
public sealed class FloorPlaybackTests
{
    static LayoutSection Layout() => new()
    {
        Floors = ["1F", "2F"],
        Elements =
        [
            new() { Id = "st-1", Type = LayoutElementType.Station, Name = "站點 1", Aliases = ["A-01"], Floor = "1F", X = 1, Y = 1, W = 2, H = 2 },
            new() { Id = "st-2", Type = LayoutElementType.Station, Name = "站點 2", Aliases = ["B-01"], Floor = "2F", X = 1, Y = 1, W = 2, H = 2 },
            new() { Id = "vh-1", Type = LayoutElementType.Vehicle, Name = "搬送機 1", Aliases = ["V1"], Floor = "2F", X = 5, Y = 1, W = 2.5, H = 1.2 },
        ],
    };

    static PlaybackViewModel Load(FloorViewSettings view, List<FloorViewSettings>? saved = null)
    {
        var layout = Layout();
        var p = new PlaybackViewModel();
        p.UseViewSettings(view, saved is null ? null : saved.Add);
        var effects = new List<EventEffect>
        {
            new(1, EffectKinds.SegmentDispatched, new Dictionary<string, IReadOnlyList<string>>
            {
                [FieldKinds.Command] = ["C-1"], [FieldKinds.Vehicle] = ["V1"], [FieldKinds.Destination] = ["B-01"],
            }),
        };
        p.Load(new WorldModel(effects, layout, "在席"), layout, [], hasStateRules: true);
        return p;
    }

    [Fact]
    public void 樓層在3D中上下堆疊_設備狀態表與詳細資訊標出樓層()
    {
        var p = Load(new FloorViewSettings());

        Assert.Equal(["1F", "2F"], p.Floors);
        Assert.Equal([new SceneFloor("1F", 0, 5), new SceneFloor("2F", 5, 5)], p.Scene.Floors); // 沒有填樓板高度：5 公尺
        Assert.Equal("2F", p.Scene.Elements.Single(e => e.Id == "st-2").Floor);
        Assert.Equal("2F", p.Equipment.Single(r => r.Id == "vh-1").Floor);
        p.SelectElementCommand.Execute(p.Elements.Single(e => e.Id == "st-1"));
        Assert.Equal("1F", p.Detail.Single(d => d.Label == "樓層").Value);
    }

    [Fact]
    public void 跟隨_預設關_開啟時設備狀態表選取元素就要求相機轉向它()
    {
        var saved = new List<FloorViewSettings>();
        var p = Load(new FloorViewSettings(), saved);
        var focused = new List<string>();
        p.FocusRequested += focused.Add;

        p.SelectedEquipment = p.Equipment.Single(r => r.Id == "st-2");
        Assert.Equal("st-2", p.SelectedElement!.Id); // 選取 Layout 上的元素
        Assert.Empty(focused); // 跟隨關：不轉向

        p.FollowFocus = true;
        Assert.Equal(new FloorViewSettings(Follow: true), saved[^1]);
        p.SelectedEquipment = p.Equipment.Single(r => r.Id == "vh-1");
        Assert.Equal(["vh-1"], focused);
    }

    [Fact]
    public void 跟隨_選取事件時轉向事件中第一個對應的元素_不改變選取()
    {
        var p = Load(new FloorViewSettings(Follow: true));
        var focused = new List<string>();
        p.FocusRequested += focused.Add;

        p.FollowEvent([new EventField(1, FieldKinds.Station, "X-99"), new EventField(1, FieldKinds.Station, "B-01")]);

        Assert.Equal(["st-2"], focused);
        Assert.Null(p.SelectedElement);
    }

    [Fact]
    public void 在3D畫面上點選元素_選取它但相機不轉向()
    {
        var p = Load(new FloorViewSettings(Follow: true));
        var focused = new List<string>();
        p.FocusRequested += focused.Add;

        p.SelectFromScene("st-2");

        Assert.Equal("st-2", p.SelectedElement!.Id);
        Assert.True(p.SceneNow.Elements.Single(e => e.Id == "st-2").Selected);
        Assert.Empty(focused);
        p.SelectFromScene(null); // 點在空白處：不改變選取
        Assert.Equal("st-2", p.SelectedElement!.Id);
    }
}
