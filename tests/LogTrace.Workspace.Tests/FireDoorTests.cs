using LogTrace.Engine.Parsing;
using LogTrace.Engine.Playback;
using LogTrace.Engine.Plants;
using LogTrace.Tests.Support;
using LogTrace.Workspace.Layout;
using LogTrace.Workspace.Playback;

namespace LogTrace.Workspace.Tests;

// 防火門的編輯與回放（ADR-0006 第 1 點 2026-11-06 補充）。LOG 樣式待確認，內容全部是虛構的。
public sealed class FireDoorEditorTests : IDisposable
{
    readonly TempFolder _tmp = new();
    readonly PlantConfigLibrary _library;
    readonly LayoutEditorViewModel _vm;

    public FireDoorEditorTests()
    {
        _library = new PlantConfigLibrary(Path.Combine(_tmp.Path, "Plants"));
        _vm = new LayoutEditorViewModel(_library, new FakeUserInteraction());
    }

    public void Dispose() => _tmp.Dispose();

    LayoutElementViewModel Add(LayoutElementType type, double x, double y)
    {
        _vm.AddElementCommand.Execute(type);
        var e = _vm.SelectedElement!;
        (e.X, e.Y) = (x, y);
        return e;
    }

    /// <summary>st-1 (1,1)、st-2 (10,1)、st-3 (1,10)；cv-1：st-1 → st-2（y = 2），cv-2：st-1 → st-3；防火門 fd-1 在 (6, 1.75) 跨過 cv-1。</summary>
    (LayoutElementViewModel Door, LayoutElementViewModel AB, LayoutElementViewModel AC) Sample()
    {
        var a = Add(LayoutElementType.Station, 1, 1);
        var b = Add(LayoutElementType.Station, 10, 1);
        var c = Add(LayoutElementType.Station, 1, 10);
        _vm.AddElementCommand.Execute(LayoutElementType.Conveyor);
        var ab = _vm.SelectedElement!;
        (ab.From, ab.To) = (a.Id, b.Id);
        _vm.AddElementCommand.Execute(LayoutElementType.Conveyor);
        var ac = _vm.SelectedElement!;
        (ac.From, ac.To) = (a.Id, c.Id);
        var door = Add(LayoutElementType.FireDoor, 6, 1.75);
        return (door, ab, ac);
    }

    [Fact]
    public void 新增防火門_可以有別名_平時狀態預設不設定()
    {
        var door = Add(LayoutElementType.FireDoor, 1, 1);

        Assert.Equal("fd-1", door.Id);
        Assert.Equal("防火門", door.TypeLabel);
        Assert.True(door.CanHaveAliases);
        Assert.Null(door.DoorState);
        Assert.Equal(0, door.DoorStateIndex);
        Assert.Equal(["不設定", "開", "關"], _vm.DoorStateChoices);
        door.DoorStateIndex = 2;
        Assert.Equal(DoorState.Closed, door.DoorState);
        Assert.Equal("平時關", door.DoorStateText);
    }

    [Fact]
    public void 依位置帶入穿過門的輸送線_手動加入與移除_存檔讀回不變()
    {
        var (door, ab, ac) = Sample();
        door.DoorState = DoorState.Open;

        _vm.SuggestDoorConveyorsCommand.Execute(null);
        Assert.Equal([ab.Id], door.Conveyors); // cv-2 沒有穿過門
        Assert.Equal([ab], _vm.SelectedDoorConveyors);

        _vm.NewDoorConveyor = ac;
        _vm.AddDoorConveyorCommand.Execute(null);
        Assert.Equal([ab.Id, ac.Id], door.Conveyors);
        _vm.RemoveDoorConveyorCommand.Execute(ac);
        Assert.Equal([ab.Id], door.Conveyors);

        _vm.SaveCommand.Execute(null);
        var saved = PlantConfigFile.Load(_library.List().Single()).Layout.Elements.Single(e => e.Id == door.Id);
        Assert.Equal(DoorState.Open, saved.DoorState);
        Assert.Equal([ab.Id], saved.Conveyors);
    }

    [Fact]
    public void 刪除輸送線時從防火門的關聯移除_復原後回來()
    {
        var (door, ab, _) = Sample();
        _vm.SuggestDoorConveyorsCommand.Execute(null);

        _vm.SelectedElement = ab;
        _vm.DeleteSelectedCommand.Execute(null);
        Assert.Empty(door.Conveyors);

        _vm.UndoCommand.Execute(null);
        Assert.Equal([ab.Id], _vm.Elements.Single(e => e.IsFireDoor).Conveyors);
    }

    [Fact]
    public void 貼上防火門_一起貼上的輸送線改成新的id_其他保留原本的id()
    {
        var (door, ab, ac) = Sample();
        door.Conveyors.Add(ab.Id);
        door.Conveyors.Add(ac.Id);

        _vm.SelectInRect(5.9, 1.9, 6.1, 2.1, add: false); // 門與穿過它的 cv-1（y = 2）
        Assert.Equal(2, _vm.SelectionCount);
        _vm.CopyCommand.Execute(null);
        // 端點沒有一起複製：輸送線連回原本的站點
        _vm.Paste(null, null);

        var pasted = _vm.Selection.Single(e => e.IsFireDoor);
        var newConveyor = _vm.Selection.Single(e => e.IsConveyor);
        Assert.NotEqual(ab.Id, newConveyor.Id);
        Assert.Equal([newConveyor.Id, ac.Id], pasted.Conveyors);
        Assert.Equal([ab.Id, ac.Id], door.Conveyors); // 原本的門不變
    }
}

public sealed class FireDoorPlaybackTests
{
    static LayoutSection Layout(DoorState? usual) => new()
    {
        Elements =
        [
            new() { Id = "st-1", Type = LayoutElementType.Station, Name = "站點 1", Aliases = ["A-01"], X = 1, Y = 1, W = 2, H = 2 },
            new() { Id = "st-2", Type = LayoutElementType.Station, Name = "站點 2", Aliases = ["A-02"], X = 10, Y = 1, W = 2, H = 2 },
            new() { Id = "cv-1", Type = LayoutElementType.Conveyor, Name = "輸送線 1", From = "st-1", To = "st-2" },
            new() { Id = "cv-2", Type = LayoutElementType.Conveyor, Name = "輸送線 2", From = "st-2", To = "st-1" },
            new() { Id = "fd-1", Type = LayoutElementType.FireDoor, Name = "防火門 1", Aliases = ["FD-01"], X = 6, Y = 1.75, W = 0.5, H = 0.5, DoorState = usual, Conveyors = ["cv-1"] },
        ],
    };

    static EventEffect E(long seq, string effect, params (string Kind, string Value)[] p) =>
        new(seq, effect, p.ToDictionary(x => x.Kind, x => (IReadOnlyList<string>)[x.Value]));

    static PlaybackViewModel Load(DoorState? usual, params EventEffect[] effects)
    {
        var layout = Layout(usual);
        var p = new PlaybackViewModel();
        p.Load(new WorldModel(effects, layout, "在席"), layout, [], hasStateRules: true);
        return p;
    }

    static readonly EventEffect[] Effects =
    [
        E(1, EffectKinds.PalletRead, (FieldKinds.Station, "A-01"), (FieldKinds.Pallet, "P-001")),
        E(2, EffectKinds.PalletRead, (FieldKinds.Station, "A-02"), (FieldKinds.Pallet, "P-001")), // 穿過（門關著時）
        E(3, EffectKinds.FireDoorOpened, (FieldKinds.FireDoor, "FD-01")),
        E(4, EffectKinds.FireDoorClosed, (FieldKinds.FireDoor, "FD-01")),
    ];

    [Fact]
    public void 沒有平時狀態也沒有事件時_未取得資訊_輸送線不阻斷()
    {
        var p = Load(null, Effects);
        p.SetMoment(2);
        var door = p.Elements.Single(e => e.Id == "fd-1");

        Assert.Equal(ElementLook.Unknown, door.Look);
        Assert.False(door.IsEstimated);
        Assert.False(door.IsInconsistent); // 不知道門的狀態：不判斷
        Assert.False(p.Elements.Single(e => e.Id == "cv-1").IsBlocked);
        p.SelectElementCommand.Execute(door);
        Assert.Equal("未取得資訊", p.Detail.Single(d => d.Label == "狀態").Value);
        Assert.Contains("未取得資訊", p.Equipment.Single(r => r.Id == "fd-1").State);
    }

    [Fact]
    public void 平時狀態為關_沒有事件時顯示推估_關聯的輸送線阻斷_穿越只在那一筆事件標示不一致()
    {
        var p = Load(DoorState.Closed, Effects);
        var door = p.Elements.Single(e => e.Id == "fd-1");
        var cv1 = p.Elements.Single(e => e.Id == "cv-1");

        p.SetMoment(1);
        Assert.Equal((ElementLook.DoorClosed, true, "推估：關"), (door.Look, door.IsEstimated, door.Summary));
        Assert.True(cv1.IsBlocked && cv1.IsBlockedEstimated);
        Assert.False(p.Elements.Single(e => e.Id == "cv-2").IsBlocked); // 沒有關聯
        Assert.False(door.IsInconsistent);

        p.SetMoment(2);
        Assert.True(door.IsInconsistent);
        Assert.Contains("P-001", door.InconsistentText);
        Assert.True(p.Equipment.Single(r => r.Id == "fd-1").IsAbnormal);
        p.SelectElementCommand.Execute(door);
        var line = p.Detail.Single(d => d.Label == "不一致");
        Assert.Equal(2L, line.Cause);
        Assert.Contains("輸送線 1", line.Value);
        Assert.Contains("推估", p.Detail.Single(d => d.Label == "狀態").Value);

        p.SetMoment(3); // 下一筆事件：不再標示；LOG 說開
        Assert.False(door.IsInconsistent);
        Assert.Equal((ElementLook.DoorOpen, false), (door.Look, door.IsEstimated));
        Assert.False(cv1.IsBlocked);
        Assert.Equal(("開", 3L), (p.Detail.Single(d => d.Label == "狀態").Value, p.Detail.Single(d => d.Label == "狀態").Cause));

        p.SetMoment(4); // LOG 說關：實線阻斷
        Assert.Equal((ElementLook.DoorClosed, false, "關"), (door.Look, door.IsEstimated, door.Summary));
        Assert.True(cv1.IsBlocked);
        Assert.False(cv1.IsBlockedEstimated);
    }
}
