using System.Text.Json;
using LogTrace.Engine.Parsing;
using LogTrace.Engine.Playback;
using LogTrace.Engine.Plants;
using LogTrace.Tests.Support;
using LogTrace.Workspace.Layout;
using LogTrace.Workspace.Playback;

namespace LogTrace.Workspace.Tests;

// 3D 回放的場景資料（ADR-0017 第 4 點，#113）：C# 產生，網頁只負責畫。內容全部是虛構的。
public sealed class PlaybackSceneTests
{
    static LayoutSection Layout() => new()
    {
        StorageCodeRule = "RRCCCLL",
        Floors = [new LayoutFloor("1F", 6), "2F"],
        Elements =
        [
            new() { Id = "st-1", Type = LayoutElementType.Station, Name = "入庫站", Aliases = ["A-01"], Floor = "1F", X = 1, Y = 1, W = 2, H = 2 },
            new() { Id = "st-2", Type = LayoutElementType.Station, Name = "出庫站", Aliases = ["B-01"], Floor = "2F", X = 1, Y = 1, W = 2, H = 2, Height = 1.1 },
            new() { Id = "rk-1", Type = LayoutElementType.Rack, Name = "料架 01", Floor = "1F", X = 10, Y = 1, W = 6, H = 1.2, Rotation = 90,
                Row = "01", ColFrom = 1, ColTo = 3, Levels = 4, LevelHeight = 1.5 },
            new() { Id = "rl-1", Type = LayoutElementType.Rail, Name = "導軌", Floor = "1F", Points = [new(1, 6), new(20, 6)] },
            new() { Id = "vh-1", Type = LayoutElementType.Vehicle, Name = "軌道車 1", Aliases = ["SHTL01"], Floor = "1F", X = 4, Y = 5.4, W = 2, H = 1.2, Rail = "rl-1" },
            new() { Id = "cv-1", Type = LayoutElementType.Conveyor, From = "st-1", To = "vh-1", Bidirectional = true, ToPoint = new(4, 6) },
            new() { Id = "ev-1", Type = LayoutElementType.Elevator, Name = "電梯 1", Aliases = ["L1"], ServedFloors = ["1F", "2F"], X = 24, Y = 1, W = 2, H = 2 },
            new() { Id = "ar-1", Type = LayoutElementType.Area, Name = "作業區", Floor = "2F", X = 0, Y = 0, W = 8, H = 8 },
        ],
    };

    static PlaybackViewModel Load(IReadOnlyList<EventEffect>? effects = null)
    {
        var layout = Layout();
        var p = new PlaybackViewModel();
        p.UseViewSettings(new FloorViewSettings(), null);
        p.Load(new WorldModel(effects ?? [], layout, "在席"), layout, [], hasStateRules: true);
        return p;
    }

    [Fact]
    public void 樓層依樓板高度上下堆疊_沒有填的樓層用5公尺()
    {
        var p = Load();

        Assert.Equal([new SceneFloor("1F", 0, 6), new SceneFloor("2F", 6, 5)], p.Scene.Floors);
    }

    [Fact]
    public void 元素的位置尺寸與旋轉沿用Layout_沒有填高度時依種類用預設值()
    {
        var s = Load().Scene.Elements.ToDictionary(e => e.Id);

        Assert.Equal(("station", "1F", 1.0, 1.0, 2.0, 2.0, 0), (s["st-1"].Type, s["st-1"].Floor, s["st-1"].X, s["st-1"].Y, s["st-1"].W, s["st-1"].H, s["st-1"].Rotation));
        Assert.Equal(PlaybackScene.DefaultHeight(LayoutElementType.Station), s["st-1"].Height);
        Assert.Equal(1.1, s["st-2"].Height); // 有填高度時用填的值
        Assert.Equal("2F", s["st-2"].Floor);
        Assert.Equal(PlaybackScene.DefaultHeight(LayoutElementType.Vehicle), s["vh-1"].Height);
        Assert.Equal("area", s["ar-1"].Type);
    }

    [Fact]
    public void 料架的高度為層數乘每層高度_導軌與輸送線帶出路徑_電梯帶出服務的樓層()
    {
        var s = Load().Scene.Elements.ToDictionary(e => e.Id);

        Assert.Equal((4, 1.5, 6.0, 90, 1, 3), (s["rk-1"].Levels!.Value, s["rk-1"].LevelHeight!.Value, s["rk-1"].Height, s["rk-1"].Rotation, s["rk-1"].ColFrom!.Value, s["rk-1"].ColTo!.Value));
        Assert.Equal([[1.0, 6.0], [20.0, 6.0]], s["rl-1"].Path);
        Assert.Equal([[2.0, 2.0], [4.0, 6.0]], s["cv-1"].Path); // 站點的中心 → 搬送機的交接點
        Assert.True(s["cv-1"].Bidirectional);
        Assert.Equal(["1F", "2F"], s["ev-1"].ServedFloors);
    }

    static EventEffect E(long seq, string effect, params (string Kind, string Value)[] p) =>
        new(seq, effect, p.ToDictionary(x => x.Kind, x => (IReadOnlyList<string>)[x.Value]));

    [Fact]
    public void 目前時刻的狀態_顏色在席貨物與電梯樓層跟著事件改變()
    {
        var p = Load(
        [
            E(1, EffectKinds.Signal, (FieldKinds.Station, "A-01"), (FieldKinds.Signal, "在席"), (FieldKinds.SignalValue, "ON")),
            E(2, EffectKinds.CommandCreated, (FieldKinds.Command, "C-1"), (FieldKinds.Pallet, "P-001"), (FieldKinds.Source, "A-01")),
            E(3, EffectKinds.ElevatorDispatched, (FieldKinds.Command, "C-1"), (FieldKinds.Elevator, "L1"), (FieldKinds.Destination, "B-01"), (FieldKinds.Source, "A-01")),
            E(4, EffectKinds.ElevatorArrived, (FieldKinds.Command, "C-1"), (FieldKinds.Destination, "B-01")),
        ]);
        SceneElementState At(string id) => p.SceneNow.Elements.Single(e => e.Id == id);

        Assert.Equal(("unknown", "unknown"), (At("st-1").Look, At("st-1").Presence)); // 案件開始：全部未知

        p.SetMoment(2);
        Assert.Equal(2, p.SceneNow.Sequence);
        Assert.Equal(("occupied", "on", true), (At("st-1").Look, At("st-1").Presence, At("st-1").Loaded));

        p.SetMoment(3);
        Assert.Null(At("ev-1").ElevatorFloor); // 移動中：沒有確定的樓層，改用來源與目的（推估）
        Assert.Equal(("1F", "2F", true, true), (At("ev-1").MovingFrom, At("ev-1").MovingTo, At("ev-1").Executing, At("ev-1").Estimated));
        Assert.True(At("ev-1").Loaded); // 載著 P-001

        p.SetMoment(4);
        Assert.Equal("2F", At("ev-1").ElevatorFloor);
        Assert.False(At("ev-1").Executing);
    }

    [Fact]
    public void 選取的元素與料架的列在狀態中標出()
    {
        var p = Load();

        p.SelectFromScene("rk-1", column: 2);

        Assert.Equal("rk-1", p.SelectedElement!.Id);
        Assert.Equal((true, 2), (p.SceneNow.Elements.Single(e => e.Id == "rk-1").Selected, p.SceneNow.Elements.Single(e => e.Id == "rk-1").SelectedColumn!.Value));
        Assert.Equal("料架 01　第 2 列（由上而下）", p.SideViewTitle);
    }

    [Fact]
    public void 傳給網頁的訊息是JSON_欄位為小駝峰_沒有值的欄位不寫出()
    {
        var p = Load();

        using var doc = JsonDocument.Parse(PlaybackScene.Message("scene", p.Scene));
        Assert.Equal("scene", doc.RootElement.GetProperty("kind").GetString());
        var station = doc.RootElement.GetProperty("data").GetProperty("elements").EnumerateArray().First(e => e.GetProperty("id").GetString() == "st-1");
        Assert.Equal("入庫站", station.GetProperty("name").GetString());
        Assert.False(station.TryGetProperty("levels", out _));
    }

    [Fact]
    public void 清除案件時場景也清空()
    {
        var p = Load();

        p.Clear("請開啟案件。");

        Assert.Empty(p.Scene.Elements);
        Assert.Empty(p.SceneNow.Elements);
    }
}

// Layout 編輯器的樓板高度（ADR-0017 第 6 點，#113）
public sealed class FloorHeightEditorTests : IDisposable
{
    readonly TempFolder _tmp = new();
    readonly PlantConfigLibrary _library;
    readonly LayoutEditorViewModel _vm;

    public FloorHeightEditorTests()
    {
        _library = new PlantConfigLibrary(Path.Combine(_tmp.Path, "Plants"));
        _vm = new LayoutEditorViewModel(_library, new FakeUserInteraction(), null, new FloorViewSettings(), null);
        _vm.PlantName = "範例廠";
    }

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void 設定目前樓層的樓板高度_改名時跟著走_存檔後讀回來相同()
    {
        _vm.AddFloorCommand.Execute(null); // 2F，成為目前樓層
        Assert.Equal("", _vm.FloorHeightText); // 沒有填：預設 5 公尺

        _vm.FloorHeightText = "6.5";
        _vm.SetFloorHeightCommand.Execute(null);
        Assert.Equal(6.5, _vm.HeightOf("2F"));

        _vm.FloorNameText = "夾層";
        _vm.RenameFloorCommand.Execute(null);
        Assert.Equal(6.5, _vm.HeightOf("夾層"));

        _vm.SelectFloorCommand.Execute("1F");
        Assert.Equal("", _vm.FloorHeightText);
        _vm.SelectFloorCommand.Execute("夾層");
        Assert.Equal("6.5", _vm.FloorHeightText);

        _vm.SaveCommand.Execute(null);
        var saved = PlantConfigFile.Load(_library.List().Single());
        Assert.Equal(6, saved.FormatVersion);
        Assert.Equal([new LayoutFloor("1F"), new LayoutFloor("夾層", 6.5)], saved.Layout.Floors);
    }

    [Fact]
    public void 樓板高度不合理時不套用_空白恢復預設_可以復原()
    {
        _vm.FloorHeightText = "0";
        _vm.SetFloorHeightCommand.Execute(null);
        Assert.Null(_vm.HeightOf("1F"));
        Assert.Contains("大於 0", _vm.Status);

        _vm.FloorHeightText = "4";
        _vm.SetFloorHeightCommand.Execute(null);
        Assert.Equal(4, _vm.HeightOf("1F"));

        _vm.UndoCommand.Execute(null);
        Assert.Null(_vm.HeightOf("1F"));

        _vm.FloorHeightText = "4";
        _vm.SetFloorHeightCommand.Execute(null);
        _vm.FloorHeightText = "";
        _vm.SetFloorHeightCommand.Execute(null);
        Assert.Null(_vm.HeightOf("1F"));
    }
}
