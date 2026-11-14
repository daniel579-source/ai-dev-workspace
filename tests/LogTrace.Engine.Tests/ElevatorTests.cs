using LogTrace.Engine.Parsing;
using LogTrace.Engine.Playback;
using LogTrace.Engine.Plants;

namespace LogTrace.Engine.Tests;

// 電梯（ADR-0005 第 11 點、ADR-0006 第 15 點，#102）。LOG 樣式待確認，以下 LOG、編號全部是虛構的。
public sealed class ElevatorTests
{
    /// <summary>1F 站點 A-01、2F 站點 B-01、3F 站點 C-01；電梯 L1 服務 1F、2F、3F。</summary>
    static LayoutSection Layout() => new()
    {
        Floors = ["1F", "2F", "3F"],
        Elements =
        [
            new() { Id = "st-1", Type = LayoutElementType.Station, Name = "1F 出入口", Aliases = ["A-01"], Floor = "1F", X = 1, Y = 1, W = 2, H = 2, Rotation = 0 },
            new() { Id = "st-2", Type = LayoutElementType.Station, Name = "2F 出入口", Aliases = ["B-01"], Floor = "2F", X = 1, Y = 1, W = 2, H = 2, Rotation = 0 },
            new() { Id = "st-3", Type = LayoutElementType.Station, Name = "3F 出入口", Aliases = ["C-01"], Floor = "3F", X = 1, Y = 1, W = 2, H = 2, Rotation = 0 },
            new() { Id = "ev-1", Type = LayoutElementType.Elevator, Name = "電梯 1", Aliases = ["L1"], ServedFloors = ["1F", "2F", "3F"], X = 4, Y = 1, W = 2, H = 2, Rotation = 0 },
        ],
    };

    static EventEffect E(long seq, string effect, params (string Kind, string Value)[] p) =>
        new(seq, effect, p.ToDictionary(x => x.Kind, x => (IReadOnlyList<string>)[x.Value]));

    static EventEffect Created(long seq) => E(seq, EffectKinds.CommandCreated, (FieldKinds.Command, "C-1"), (FieldKinds.Pallet, "P-001"), (FieldKinds.Source, "A-01"));

    static EventEffect Dispatch(long seq, string dest, string? source = null) => source is null
        ? E(seq, EffectKinds.ElevatorDispatched, (FieldKinds.Command, "C-1"), (FieldKinds.Elevator, "L1"), (FieldKinds.Destination, dest))
        : E(seq, EffectKinds.ElevatorDispatched, (FieldKinds.Command, "C-1"), (FieldKinds.Elevator, "L1"), (FieldKinds.Destination, dest), (FieldKinds.Source, source));

    [Fact]
    public void 載貨從1F到2F_移動中維持出發樓層_抵達後依LOG_貨物到目的地()
    {
        var world = new WorldModel([Created(1), Dispatch(2, "B-01", "A-01"), E(3, EffectKinds.ElevatorArrived, (FieldKinds.Command, "C-1"), (FieldKinds.Destination, "B-01"))], Layout(), "在席");

        Assert.Empty(world.StateAt(1).Elevators); // 第一次事件前：未取得資訊

        var moving = world.StateAt(2);
        var e = moving.Elevators["ev-1"];
        Assert.Equal((VehicleWork.Executing, "C-1", "1F", "2F"), (e.Work, e.Command, e.FromFloor, e.ToFloor));
        Assert.Equal(("1F", ElevatorFloorBasis.EstimatedFromSource), (e.Floor, e.FloorBasis!.Value)); // 目前樓層未知：依來源推估
        Assert.Equal((LocationKind.Elevator, "ev-1"), (moving.Pallets["P-001"].Location, moving.Pallets["P-001"].LocationId));
        Assert.Equal(("ev-1", CommandStatus.SegmentRunning), (moving.Commands["C-1"].Vehicle, moving.Commands["C-1"].Status));
        Assert.Empty(moving.Vehicles); // 不放在搬送機的狀態中

        var arrived = world.Final.Elevators["ev-1"];
        Assert.Equal((VehicleWork.Idle, "2F", 3L, ElevatorFloorBasis.Arrived), (arrived.Work, arrived.Floor, arrived.FloorCause!.Value, arrived.FloorBasis!.Value));
        Assert.Null(arrived.FromFloor);
        Assert.Equal((LocationKind.Station, "st-2"), (world.Final.Pallets["P-001"].Location, world.Final.Pallets["P-001"].LocationId));
    }

    [Fact]
    public void 已知目前樓層時不從來源推估_沒有來源也沒有目前樓層時未取得資訊()
    {
        var world = new WorldModel(
        [
            Created(1), Dispatch(2, "B-01"),                                   // 沒有來源、目前樓層未知
            E(3, EffectKinds.ElevatorArrived, (FieldKinds.Command, "C-1"), (FieldKinds.Destination, "B-01")),
            Dispatch(4, "C-01", "A-01"),                                       // 已知在 2F：來源 A-01（1F）不採用
        ], Layout(), "在席");

        var first = world.StateAt(2).Elevators["ev-1"];
        Assert.Null(first.Floor);
        Assert.Equal((null, "2F"), (first.FromFloor, first.ToFloor));
        var second = world.StateAt(4).Elevators["ev-1"];
        Assert.Equal(("2F", "2F", "3F", ElevatorFloorBasis.Arrived), (second.Floor, second.FromFloor, second.ToFloor, second.FloorBasis!.Value));
    }

    [Fact]
    public void 作業完成與取消_停在出發樓層_回到閒置()
    {
        var finished = new WorldModel([Created(1), Dispatch(2, "B-01", "A-01"), E(3, EffectKinds.ElevatorFinished, (FieldKinds.Command, "C-1"))], Layout(), "在席");
        var f = finished.Final.Elevators["ev-1"];
        Assert.Equal((VehicleWork.Idle, "1F", ElevatorFloorBasis.FinishedNoLocation), (f.Work, f.Floor, f.FloorBasis!.Value));
        Assert.Equal(CommandStatus.SegmentFinished, finished.Final.Commands["C-1"].Status);

        var cancelled = new WorldModel([Created(1), Dispatch(2, "B-01", "A-01"), E(3, EffectKinds.CommandCancelled, (FieldKinds.Command, "C-1"))], Layout(), "在席");
        var c = cancelled.Final.Elevators["ev-1"];
        Assert.Equal((VehicleWork.Idle, "1F", ElevatorFloorBasis.Cancelled), (c.Work, c.Floor, c.FloorBasis!.Value));
    }

    [Fact]
    public void 警報可以指向電梯_電梯欄位列入未對應的編號()
    {
        var world = new WorldModel([E(1, EffectKinds.AlarmRaised, (FieldKinds.Elevator, "L1"), (FieldKinds.AlarmCode, "E-9"))], Layout(), "在席");

        Assert.Equal("E-9", world.Final.Alarms["ev-1"].Code);
        Assert.Single(world.AlarmPeriods());
        Assert.Contains(FieldKinds.Elevator, FieldKinds.Identifiers);
        Assert.Null(FieldExtractor.Check(new ParseRule("派給電梯（虛構）", null, null, @"(?<command>C-\d+) (?<elevator>L\d) → (?<destination>\S+)", EffectKinds.ElevatorDispatched)));
        Assert.Contains("需要欄位", FieldExtractor.Check(new ParseRule("缺電梯（虛構）", null, null, @"(?<command>C-\d+) → (?<destination>\S+)", EffectKinds.ElevatorDispatched)));
    }

    [Fact]
    public void 存檔讀回服務的樓層_格式版本5_電梯不寫floor_檢查至少兩層與樓層存在()
    {
        var doc = new PlantConfigDocument { Plant = "範例廠", Layout = Layout() };

        var json = PlantConfigFile.Serialize(doc);
        Assert.Contains("\"formatVersion\": 5", json);
        Assert.Contains("\"type\": \"elevator\"", json);
        var ev = PlantConfigFile.Deserialize(json).Layout.Elements.Single(e => e.Id == "ev-1");
        Assert.Equal(["1F", "2F", "3F"], ev.ServedFloors);
        Assert.Null(ev.Floor);
        Assert.True(LayoutElement.CanHaveAliases(LayoutElementType.Elevator));
        Assert.False(LayoutElement.CanBeConveyorEnd(LayoutElementType.Elevator));
        Assert.Equal(["1F", "2F", "3F"], LayoutFloors.FloorsOf(ev, ["1F", "2F", "3F"], _ => null));

        var bad = Layout();
        bad.Elements.Single(e => e.Id == "ev-1").ServedFloors = ["1F", "9F"];
        bad.Elements.Add(new() { Id = "ev-2", Type = LayoutElementType.Elevator, ServedFloors = ["2F"], X = 8, Y = 1, W = 2, H = 2, Rotation = 0 });
        bad.Elements.Add(new() { Id = "cv-1", Type = LayoutElementType.Conveyor, From = "st-1", To = "ev-1" });
        var issues = PlantConfigFile.Validate(new PlantConfigDocument { Plant = "範例廠", Layout = bad });
        Assert.Contains(issues, i => i.Message.Contains("「9F」不在樓層清單中") && i.ElementIds.SequenceEqual(["ev-1"]));
        Assert.Contains(issues, i => i.Message.Contains("至少要服務兩個樓層") && i.ElementIds.SequenceEqual(["ev-2"]));
        Assert.Contains(issues, i => i.Message.Contains("兩端必須是站點、Conv 或搬送機") && i.ElementIds.SequenceEqual(["cv-1"]));
    }

    [Fact]
    public void 快照推導與從頭推導相同()
    {
        var effects = new List<EventEffect> { Created(1) };
        for (int i = 0; i < 12; i++)
            effects.Add(i % 2 == 0 ? Dispatch(i + 2, i % 4 == 0 ? "B-01" : "C-01", "A-01")
                : E(i + 2, EffectKinds.ElevatorArrived, (FieldKinds.Command, "C-1"), (FieldKinds.Destination, i % 4 == 1 ? "B-01" : "C-01")));
        var world = new WorldModel(effects, Layout(), "在席", snapshotEvery: 3);

        for (long seq = 0; seq <= 14; seq++)
            Assert.Equal(world.ReplayFromStart(seq).Fingerprint(), world.StateAt(seq).Fingerprint());
    }
}
