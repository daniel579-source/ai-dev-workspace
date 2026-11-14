using LogTrace.Engine.Parsing;
using LogTrace.Engine.Playback;
using LogTrace.Engine.Plants;
using LogTrace.Tests.Support;
using LogTrace.Workspace.Layout;
using LogTrace.Workspace.Playback;

namespace LogTrace.Workspace.Tests;

// RM 與排的配置表的編輯（ADR-0019，#123）。內容全部是虛構的。
public sealed class RmEditorTests : IDisposable
{
    readonly TempFolder _tmp = new();
    readonly PlantConfigLibrary _library;
    readonly LayoutEditorViewModel _vm;

    public RmEditorTests()
    {
        _library = new PlantConfigLibrary(Path.Combine(_tmp.Path, "Plants"));
        _vm = new LayoutEditorViewModel(_library, new FakeUserInteraction()) { PlantName = "範例廠" };
    }

    public void Dispose() => _tmp.Dispose();

    LayoutElementViewModel AddRm()
    {
        _vm.AddElementCommand.Execute(LayoutElementType.Rm);
        return _vm.SelectedElement!;
    }

    [Fact]
    public void 新增RM_預設尺寸與名稱_可以加別名與指定導軌_不能當輸送線端點()
    {
        _vm.AddElementCommand.Execute(LayoutElementType.Rail);
        var rail = _vm.SelectedElement!;
        var rm = AddRm();

        Assert.Equal(("RM", "rm-1", 2d, 1.2, 12.5), (rm.TypeLabel, rm.Id, rm.W, rm.H, rm.Height));
        Assert.True(rm.CanHaveAliases && rm.CanHaveRail);
        Assert.Contains(rail, _vm.RailChoices);
        rm.Rail = rail.Id;
        Assert.DoesNotContain(rm, _vm.ConveyorEndpoints);
        Assert.Contains(LayoutElementType.Rm, _vm.AddableTypes);
    }

    [Fact]
    public void 在RM的屬性面板編輯負責的排_存檔寫入排的配置表與格式版本7()
    {
        var rm = AddRm();
        _vm.AddRmRowCommand.Execute(null);
        _vm.AddRmRowCommand.Execute(null);
        Assert.Equal(["1", "2"], _vm.RmRows.Select(r => r.Row));
        (_vm.RmRows[0].Depth, _vm.RmRows[0].Pair) = (RowItemViewModel.Deep, "2");
        (_vm.RmRows[1].Depth, _vm.RmRows[1].Pair) = (RowItemViewModel.Shallow, "1");

        _vm.SaveCommand.Execute(null);

        var saved = PlantConfigFile.Load(_library.List().Single());
        Assert.Equal(7, saved.FormatVersion);
        Assert.Equal([new LayoutRow("1", rm.Id, RowDepth.Deep, "2"), new LayoutRow("2", rm.Id, RowDepth.Shallow, "1")], saved.Layout.Rows);

        _vm.SelectedElement = null;
        Assert.Empty(_vm.RmRows); // 只在選取 RM 時列出
        _vm.SelectedElement = _vm.Elements.Single(e => e.Id == rm.Id);
        Assert.Equal(2, _vm.RmRows.Count);
    }

    [Fact]
    public void 刪除RM時它的排一併移除_復原時恢復()
    {
        var rm = AddRm();
        _vm.AddRmRowCommand.Execute(null);

        _vm.DeleteSelectedCommand.Execute(null);
        _vm.UndoCommand.Execute(null); // 存檔後就不能復原，所以先復原
        _vm.SelectedElement = _vm.Elements.Single(e => e.Id == rm.Id);
        Assert.Equal(["1"], _vm.RmRows.Select(r => r.Row));

        _vm.DeleteSelectedCommand.Execute(null);
        _vm.SaveCommand.Execute(null);
        Assert.Null(PlantConfigFile.Load(_library.List().Single()).Layout.Rows);
    }
}

// RM 與雙深料架的回放（ADR-0019 第 5、7 點）
public sealed class RmPlaybackTests
{
    static readonly DateTime T0 = new(2026, 1, 1, 8, 0, 0);

    static EventEffect E(long seq, string effect, params (string Kind, string Value)[] p) =>
        new(seq, effect, p.ToDictionary(x => x.Kind, x => (IReadOnlyList<string>)[x.Value]));

    static PlaybackViewModel Load()
    {
        var layout = new LayoutSection
        {
            StorageCodeRule = "RCCCLL",
            Elements =
            [
                new() { Id = "rl-1", Type = LayoutElementType.Rail, Points = RailPath.StraightTemplate(0, 5) },
                new() { Id = "rm-1", Type = LayoutElementType.Rm, Name = "RM 1", Aliases = ["RM01"], X = 8, Y = 4.4, W = 2, H = 1.2, Rotation = 0, Rail = "rl-1" },
                new() { Id = "eq-1", Type = LayoutElementType.Dock, Name = "Conv P1", Aliases = ["P1"], X = 0, Y = 6, W = 2, H = 2, Rotation = 0 },
                new() { Id = "rk-1", Type = LayoutElementType.Rack, Name = "深排", Row = "1", ColFrom = 1, ColTo = 10, Levels = 2, LevelHeight = 1.5, X = 0, Y = 1, W = 20, H = 1.2, Rotation = 0 },
                new() { Id = "rk-2", Type = LayoutElementType.Rack, Name = "淺排", Row = "2", ColFrom = 1, ColTo = 10, Levels = 2, LevelHeight = 1.5, X = 0, Y = 2.4, W = 20, H = 1.2, Rotation = 0 },
            ],
            Rows = [new LayoutRow("1", "rm-1", RowDepth.Deep, "2"), new LayoutRow("2", "rm-1", RowDepth.Shallow, "1")],
        };
        var effects = new List<EventEffect>
        {
            E(1, EffectKinds.CommandCreated, (FieldKinds.Command, "C-1"), (FieldKinds.Pallet, "P-001"), (FieldKinds.Source, "P1")),
            E(2, EffectKinds.SegmentDispatched, (FieldKinds.Command, "C-1"), (FieldKinds.Rm, "RM01"), (FieldKinds.Destination, "100302"), (FieldKinds.Source, "P1")),
            E(3, EffectKinds.SegmentArrived, (FieldKinds.Command, "C-1"), (FieldKinds.Destination, "100302")),
        };
        var events = effects.Select(x => new Engine.Events.LogEvent(x.Sequence, new Engine.Events.StableEventId("PLC", "20260101", "PLC/log.txt", (int)x.Sequence),
            T0.AddSeconds(x.Sequence * 10), "", "INFO", "PLC", "PLCLOG", $"虛構事件 {x.Sequence}",
            new Engine.Events.EventSource("PLC/log.txt", (int)x.Sequence, (int)x.Sequence, 0, 0), Engine.Events.EventFlags.None)).ToList();
        var p = new PlaybackViewModel();
        p.Load(new WorldModel(effects, layout, "在席"), layout, events, hasStateRules: true);
        return p;
    }

    [Fact]
    public void RM列在設備狀態表_執行中載著貨物_3D帶出位置與載貨台高度()
    {
        var p = Load();

        p.SetMoment(3);
        var state = p.SceneNow.Elements.Single(e => e.Id == "rm-1");
        Assert.Equal((5d, 1.5), (state.X!.Value, state.Lift!.Value)); // 第 3 列中心、第 2 層
        Assert.Equal("rm", p.Scene.Elements.Single(e => e.Id == "rm-1").Type);
        Assert.Contains(p.Equipment, r => r.Id == "rm-1" && r.TypeLabel == "RM");

        p.SetMoment(2, T0.AddSeconds(25)); // 派送 20 秒、抵達 30 秒：一半
        state = p.SceneNow.Elements.Single(e => e.Id == "rm-1");
        Assert.True(state.Loaded && state.Executing && state.Estimated);
        p.SelectElementById("rm-1");
        Assert.StartsWith("約 0.8 公尺（推估", p.Detail.Single(d => d.Label == "載貨台").Value);
    }

    [Fact]
    public void 搬送中貨物的標示_預設命令編號_可切換棧板編號並記在本機_放下後不標示()
    {
        var p = Load();
        var saved = new List<FloorViewSettings>();
        p.UseViewSettings(new FloorViewSettings(), saved.Add);

        p.SetMoment(2); // 貨物在 RM 上
        Assert.Equal("C-1", p.SceneNow.Elements.Single(e => e.Id == "rm-1").CargoLabel);

        p.CargoLabelIndex = 1;
        Assert.True(p.CargoByPallet);
        Assert.Equal(new FloorViewSettings(CargoByPallet: true), saved[^1]);
        Assert.Equal("P-001", p.SceneNow.Elements.Single(e => e.Id == "rm-1").CargoLabel);

        p.SetMoment(3); // 放進儲位：不再是搬送中的貨物
        Assert.Null(p.SceneNow.Elements.Single(e => e.Id == "rm-1").CargoLabel);

        var reopened = Load();
        reopened.UseViewSettings(saved[^1], null);
        Assert.Equal(1, reopened.CargoLabelIndex); // 下次開啟時沿用
    }

    [Fact]
    public void 雙深料架的側視圖每層分淺深兩格()
    {
        var p = Load();
        p.SetMoment(3);

        var deep = p.Elements.Single(e => e.Id == "rk-1");
        p.SelectRackCell(deep.Cells.Single(c => c.Column == 3));

        Assert.Contains("淺＝第 2 排、深＝第 1 排", p.SideViewTitle);
        Assert.Equal([(2, "淺", "200302"), (2, "深", "100302"), (1, "淺", "200301"), (1, "深", "100301")],
            p.SideView.Select(l => (l.Level, l.Depth!, l.StorageCode!)));
        Assert.Equal("P-001", p.SideView.Single(l => l.StorageCode == "100302").Pallet);
    }
}
