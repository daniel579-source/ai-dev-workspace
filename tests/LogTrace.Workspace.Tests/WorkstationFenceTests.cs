using LogTrace.Engine.Events;
using LogTrace.Engine.Parsing;
using LogTrace.Engine.Playback;
using LogTrace.Engine.Plants;
using LogTrace.Tests.Support;
using LogTrace.Workspace.Layout;
using LogTrace.Workspace.Playback;

namespace LogTrace.Workspace.Tests;

// 工站點、圍欄、圍欄門、電梯閘門與預設高度（ADR-0020）。內容全部是虛構的。
public sealed class WorkstationFenceTests : IDisposable
{
    readonly TempFolder _tmp = new();

    public void Dispose() => _tmp.Dispose();

    static readonly DateTime T0 = new(2030, 1, 7, 10, 0, 0);

    static LogEvent Event(long seq) =>
        new(seq, new StableEventId("Server", "20300107", "Server/log.txt", (int)seq), T0.AddSeconds(seq * 10), "", "INFO", "Server", "WCSDebug", $"虛構事件 {seq}",
            new EventSource("Server/log.txt", (int)seq, (int)seq, 0, 0), EventFlags.None);

    static EventEffect E(long seq, string effect, params (string Kind, string Value)[] p) =>
        new(seq, effect, p.ToDictionary(x => x.Kind, x => (IReadOnlyList<string>)[x.Value]));

    static LayoutSection Layout() => new()
    {
        Floors = ["1F", "2F"],
        Elements =
        [
            new() { Id = "ev-1", Type = LayoutElementType.Elevator, Name = "電梯", Aliases = ["EV01"], X = 10, Y = 10, W = 2, H = 2, Rotation = 0, ServedFloors = ["1F", "2F"] },
            new() { Id = "st-1", Type = LayoutElementType.Station, Name = "左站", Aliases = ["A-01"], X = 8, Y = 10, W = 2, H = 2, Rotation = 0, Floor = "1F" },
            new() { Id = "ws-1", Type = LayoutElementType.Workstation, Name = "右工站", Aliases = ["W-01"], X = 12, Y = 10, W = 2, H = 2, Rotation = 0, Floor = "2F" },
            new() { Id = "eq-1", Type = LayoutElementType.Dock, Name = "Conv", Aliases = ["EQ1"], X = 0, Y = 0, W = 2, H = 2, Rotation = 0 },
            new() { Id = "vh-1", Type = LayoutElementType.Vehicle, Name = "STV", Aliases = ["V1"], X = 0, Y = 4, W = 2, H = 1, Rotation = 0 },
            new() { Id = "fc-1", Type = LayoutElementType.Fence, Name = "圍欄", Points = [new RailPoint(0, 20), new RailPoint(8, 20), new RailPoint(8, 26)] },
            new() { Id = "fg-1", Type = LayoutElementType.FenceGate, Name = "圍欄門", X = 8, Y = 20, W = 1, H = 0.1, Rotation = 90 },
        ],
    };

    [Fact]
    public void 場景_設備預設高度0點8_圍欄折線2點5_圍欄門2點5_電梯兩側對接都有閘門()
    {
        var scene = PlaybackScene.LayoutOf(Layout());
        SceneElement El(string id) => scene.Elements.Single(e => e.Id == id);

        Assert.Equal([0.8, 0.8, 0.8, 0.8], new[] { "st-1", "ws-1", "eq-1", "vh-1" }.Select(id => El(id).Height));
        Assert.Equal(0.8, PlaybackScene.DefaultHeight(LayoutElementType.Conveyor));
        Assert.Equal(("fence", 2.5, 3), (El("fc-1").Type, El("fc-1").Height, El("fc-1").Path!.Count));
        Assert.Equal(("fenceGate", 2.5), (El("fg-1").Type, El("fg-1").Height));
        Assert.Equal("workstation", El("ws-1").Type);
        Assert.Equal([ElevatorGates.XMin, ElevatorGates.XMax], El("ev-1").Gates);
        Assert.Equal(3, PlaybackScene.DefaultHeight(LayoutElementType.FireDoor)); // 防火門不變
    }

    [Fact]
    public void 回放_電梯抵達後朝目的地的閘門畫成開_推估_派送後關上_工站點比照站點()
    {
        var layout = Layout();
        var effects = new List<EventEffect>
        {
            E(1, EffectKinds.ElevatorDispatched, (FieldKinds.Command, "C-1"), (FieldKinds.Elevator, "EV01"), (FieldKinds.Destination, "W-01")),
            E(2, EffectKinds.ElevatorArrived, (FieldKinds.Command, "C-1"), (FieldKinds.Destination, "W-01")),
            E(3, EffectKinds.Signal, (FieldKinds.Station, "W-01"), (FieldKinds.Signal, "在席"), (FieldKinds.SignalValue, "ON")),
            E(4, EffectKinds.ElevatorDispatched, (FieldKinds.Command, "C-2"), (FieldKinds.Elevator, "EV01"), (FieldKinds.Destination, "A-01")),
        };
        var p = new PlaybackViewModel();
        p.Load(new WorldModel(effects, layout, "在席"), layout, Enumerable.Range(1, 4).Select(i => Event(i)).ToList(), hasStateRules: true);
        SceneElementState Now(string id) => p.SceneNow.Elements.Single(e => e.Id == id);

        p.SetMoment(1);
        Assert.Null(Now("ev-1").OpenGate); // 移動中：關著
        p.SetMoment(3);
        Assert.Equal(ElevatorGates.XMax, Now("ev-1").OpenGate); // 工站點在右側
        Assert.Equal("on", Now("ws-1").Presence);
        p.SelectElementCommand.Execute(p.Elements.Single(e => e.Id == "ev-1"));
        Assert.Contains("朝目的地的那一處開著（推估", p.Detail.Single(d => d.Label == "閘門").Value);
        p.SetMoment(4);
        Assert.Null(Now("ev-1").OpenGate); // 下一次派送：關上
        Assert.Contains(p.Equipment, r => r.Id == "ws-1" && r.State.Contains("在席 ON"));
        Assert.DoesNotContain(p.Equipment, r => r.Id is "fc-1" or "fg-1"); // 圍欄與圍欄門不推導狀態
    }

    [Fact]
    public void 編輯器_新增圍欄為折線_新增圍欄門預設寬1公尺高2點5_新增工站點可以填別名()
    {
        var vm = new LayoutEditorViewModel(new PlantConfigLibrary(Path.Combine(_tmp.Path, "Plants")), new FakeUserInteraction());

        vm.AddElementCommand.Execute(LayoutElementType.Fence);
        var fence = vm.SelectedElement!;
        Assert.Equal(("fc-1", "圍欄", 2), (fence.Id, fence.TypeLabel, fence.RailPoints.Count));
        Assert.True(fence.IsPath);
        Assert.False(fence.IsRail); // 不會出現在搬送機的導軌選單

        vm.AddElementCommand.Execute(LayoutElementType.FenceGate);
        var gate = vm.SelectedElement!;
        Assert.Equal(("fg-1", "圍欄門", 1d, 2.5), (gate.Id, gate.TypeLabel, gate.W, gate.ToModel().Height!.Value));
        Assert.False(gate.CanHaveAliases);

        vm.AddElementCommand.Execute(LayoutElementType.Workstation);
        var ws = vm.SelectedElement!;
        Assert.Equal(("ws-1", "工站點", 0.8), (ws.Id, ws.TypeLabel, ws.ToModel().Height!.Value));
        Assert.True(ws.CanHaveAliases);

        vm.AddElementCommand.Execute(LayoutElementType.Vehicle);
        vm.AddElementCommand.Execute(LayoutElementType.Rail);
        vm.SelectedElement = vm.Elements.Single(e => e.Id == "vh-1");
        Assert.Equal(["rl-1"], vm.RailChoices.Select(r => r.Id)); // 只有導軌，不含圍欄
    }

    [Fact]
    public void EQ_門朝緊鄰的Conv_Conv上有棧板等待完成回報時門開_推估()
    {
        var layout = new LayoutSection
        {
            Elements =
            [
                new() { Id = "eq-1", Type = LayoutElementType.Dock, Name = "Conv", Aliases = ["EQ901"], X = 0, Y = 0, W = 2, H = 2, Rotation = 0 },
                new() { Id = "eqp-1", Type = LayoutElementType.Eq, Name = "EQ", X = 2.1, Y = 0, W = 1.5, H = 1.5, Rotation = 0 },
                new() { Id = "st-1", Type = LayoutElementType.Station, Name = "站", Aliases = ["A-01"], X = 0, Y = 5, W = 2, H = 2, Rotation = 0 },
            ],
        };
        var effects = new List<EventEffect>
        {
            E(1, EffectKinds.CommandCreated, (FieldKinds.Command, "C-1"), (FieldKinds.Pallet, "P-1")),
            E(2, EffectKinds.PalletRead, (FieldKinds.Station, "A-01"), (FieldKinds.Pallet, "P-1")),
            E(3, EffectKinds.SegmentArrived, (FieldKinds.Command, "C-1"), (FieldKinds.Destination, "EQ901")),
            E(4, EffectKinds.CommandCompleted, (FieldKinds.Command, "C-1")),
        };
        var p = new PlaybackViewModel();
        p.Load(new WorldModel(effects, layout, "在席"), layout, Enumerable.Range(1, 4).Select(i => Event(i)).ToList(), hasStateRules: true);

        Assert.Equal(ElevatorGates.XMin, p.Scene.Elements.Single(e => e.Id == "eqp-1").Door);
        Assert.Equal(1.8, PlaybackScene.DefaultHeight(LayoutElementType.Eq));
        p.SetMoment(2);
        Assert.False(p.SceneNow.Elements.Single(e => e.Id == "eqp-1").DoorOpen);
        p.SetMoment(3); // 抵達 Conv，等待完成回報
        Assert.True(p.SceneNow.Elements.Single(e => e.Id == "eqp-1").DoorOpen);
        p.SelectElementCommand.Execute(p.Elements.Single(e => e.Id == "eqp-1"));
        Assert.Contains("開著（推估", p.Detail.Single(d => d.Label == "門").Value);
        p.SetMoment(4); // 完成回報：已交出
        Assert.False(p.SceneNow.Elements.Single(e => e.Id == "eqp-1").DoorOpen);
        Assert.DoesNotContain(p.Equipment, r => r.Id == "eqp-1"); // EQ 不推導狀態
    }
}
