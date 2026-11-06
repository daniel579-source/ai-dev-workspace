using LogTrace.Engine.Parsing;
using LogTrace.Engine.Playback;
using LogTrace.Engine.Plants;
using LogTrace.Tests.Support;

namespace LogTrace.Engine.Tests;

// 防火門：開／關兩種狀態、平時狀態（推估）、關聯的輸送線（ADR-0006 第 1 點 2026-11-06 補充）。
// LOG 樣式待確認，以下 LOG、編號與棧板全部是虛構的。
public sealed class FireDoorTests : IDisposable
{
    readonly TempFolder _tmp = new();

    public void Dispose() => _tmp.Dispose();

    static LayoutSection Layout(DoorState? usual = null) => new()
    {
        Elements =
        [
            new() { Id = "st-1", Type = LayoutElementType.Station, Name = "站點 1", Aliases = ["A-01"], X = 1, Y = 1, W = 2, H = 2, Rotation = 0 },
            new() { Id = "st-2", Type = LayoutElementType.Station, Name = "站點 2", Aliases = ["A-02"], X = 10, Y = 1, W = 2, H = 2, Rotation = 0 },
            new() { Id = "st-3", Type = LayoutElementType.Station, Name = "站點 3", Aliases = ["A-03"], X = 1, Y = 8, W = 2, H = 2, Rotation = 0 },
            new() { Id = "cv-1", Type = LayoutElementType.Conveyor, From = "st-1", To = "st-2" },
            new() { Id = "cv-2", Type = LayoutElementType.Conveyor, From = "st-1", To = "st-3" },
            new()
            {
                Id = "fd-1", Type = LayoutElementType.FireDoor, Name = "防火門 1", Aliases = ["FD-01"], X = 6, Y = 1.75, W = 0.5, H = 0.5, Rotation = 0,
                DoorState = usual, Conveyors = ["cv-1"],
            },
        ],
    };

    static EventEffect E(long seq, string effect, params (string Kind, string Value)[] p) =>
        new(seq, effect, p.ToDictionary(x => x.Kind, x => (IReadOnlyList<string>)[x.Value]));

    static EventEffect Read(long seq, string station, string pallet = "P-001") =>
        E(seq, EffectKinds.PalletRead, (FieldKinds.Station, station), (FieldKinds.Pallet, pallet));

    static EventEffect Door(long seq, bool open) =>
        E(seq, open ? EffectKinds.FireDoorOpened : EffectKinds.FireDoorClosed, (FieldKinds.FireDoor, "FD-01"));

    [Fact]
    public void 存檔讀回_防火門的平時狀態與關聯輸送線不變_格式版本為3()
    {
        var doc = new PlantConfigDocument { Plant = "範例廠", Layout = Layout(DoorState.Closed) };
        var path = Path.Combine(_tmp.Path, "範例廠.json");

        PlantConfigFile.Save(doc, path);
        var fd = PlantConfigFile.Load(path).Layout.Elements.Single(e => e.Id == "fd-1");

        Assert.Equal(LayoutElementType.FireDoor, fd.Type);
        Assert.Equal(DoorState.Closed, fd.DoorState);
        Assert.Equal(["cv-1"], fd.Conveyors);
        Assert.Equal(["FD-01"], fd.Aliases);
        var json = File.ReadAllText(path);
        Assert.Contains("\"formatVersion\": 3", json); // 舊版程式不認得 fireDoor：明確提示改用新版
        Assert.Contains("\"type\": \"fireDoor\"", json);
        Assert.Contains("\"doorState\": \"closed\"", json);
    }

    [Fact]
    public void 沒有防火門時格式版本不變_不寫出防火門的欄位()
    {
        var layout = Layout();
        layout.Elements.RemoveAll(e => e.Type == LayoutElementType.FireDoor);

        var json = PlantConfigFile.Serialize(new PlantConfigDocument { Plant = "範例廠", Layout = layout });

        Assert.Contains("\"formatVersion\": 1", json);
        Assert.DoesNotContain("doorState", json);
        Assert.DoesNotContain("conveyors", json);
    }

    [Fact]
    public void 存檔前檢查_防火門可以有別名_關聯不存在或重複的輸送線時擋下()
    {
        var layout = Layout();
        var fd = layout.Elements.Single(e => e.Id == "fd-1");
        fd.Conveyors = ["cv-1", "cv-1", "cv-9", "st-1"];

        var issues = PlantConfigFile.Validate(new PlantConfigDocument { Plant = "範例廠", Layout = layout });

        Assert.Equal(3, issues.Count);
        Assert.All(issues, i => Assert.Equal(["fd-1"], i.ElementIds));
        Assert.Contains(issues, i => i.Message.Contains("「cv-9」不存在"));
        Assert.Contains(issues, i => i.Message.Contains("「st-1」不存在")); // 不是輸送線
        Assert.Contains(issues, i => i.Message.Contains("重複關聯輸送線「cv-1」"));
        Assert.True(LayoutElement.CanHaveAliases(LayoutElementType.FireDoor));
        Assert.False(LayoutElement.CanBeConveyorEnd(LayoutElementType.FireDoor));
    }

    [Fact]
    public void 規則抽出防火門_開關兩種效果都需要防火門欄位()
    {
        var rule = new ParseRule("防火門關（虛構）", "PLC", null, @"^\[(?<fireDoor>FD-[0-9]{2})\] CLOSE$", EffectKinds.FireDoorClosed);
        Assert.Null(FieldExtractor.Check(rule));
        Assert.Contains("需要欄位", FieldExtractor.Check(new ParseRule("缺防火門（虛構）", null, null, @"\[(?<station>A-[0-9]{2})\] OPEN", EffectKinds.FireDoorOpened)));

        var (fields, effects) = new FieldExtractor([rule]).ExtractWithEffects(5, "PLC", "PLCLOG", "[FD-01] CLOSE");

        Assert.Contains(new EventField(5, FieldKinds.FireDoor, "FD-01"), fields);
        Assert.Equal(EffectKinds.FireDoorClosed, Assert.Single(effects).Effect);
        Assert.Contains(FieldKinds.FireDoor, FieldKinds.Identifiers); // 未對應的編號也列出防火門
    }

    [Fact]
    public void 第一次事件之前沒有LOG狀態_之後依事件開關並記錄造成的事件()
    {
        var world = new WorldModel([Door(2, open: false), Door(4, open: true), E(5, EffectKinds.FireDoorClosed, (FieldKinds.FireDoor, "FD-99"))], Layout(), "在席");

        Assert.Empty(world.StateAt(1).Doors); // 未取得資訊（畫面改用平時狀態或顯示未取得資訊）
        Assert.Equal((DoorState.Closed, 2L), (world.StateAt(3).Doors["fd-1"].State, world.StateAt(3).Doors["fd-1"].StateCause));
        Assert.Equal((DoorState.Open, 4L), (world.Final.Doors["fd-1"].State, world.Final.Doors["fd-1"].StateCause));
        Assert.Single(world.Final.Doors); // 對應不到 Layout 的編號不建立
    }

    [Fact]
    public void 門關著時貨物在關聯輸送線兩端之間移動_記錄穿越_兩個方向都算()
    {
        var world = new WorldModel([Door(1, open: false), Read(2, "A-01"), Read(3, "A-02"), Read(4, "A-01")], Layout(), "在席");

        Assert.Null(world.StateAt(2).Doors["fd-1"].LastCrossing); // 第一次出現，沒有移動
        Assert.Equal(new DoorCrossing("P-001", "cv-1", 3), world.StateAt(3).Doors["fd-1"].LastCrossing);
        Assert.Equal(new DoorCrossing("P-001", "cv-1", 4), world.Final.Doors["fd-1"].LastCrossing); // 反方向也算
    }

    [Fact]
    public void 門開著_或貨物走的不是關聯的輸送線_不算穿越()
    {
        var world = new WorldModel([Door(1, open: false), Read(2, "A-01"), Read(3, "A-03"), Door(4, open: true), Read(5, "A-01"), Read(6, "A-02")], Layout(), "在席");

        Assert.Null(world.Final.Doors["fd-1"].LastCrossing);
    }

    [Fact]
    public void 平時狀態為關_還沒有LOG事件時也算穿越_LOG說開之後不算()
    {
        var world = new WorldModel([Read(1, "A-01"), Read(2, "A-02"), Door(3, open: true), Read(4, "A-01")], Layout(DoorState.Closed), "在席");

        Assert.Equal(new DoorCrossing("P-001", "cv-1", 2), world.StateAt(2).Doors["fd-1"].LastCrossing);
        Assert.Null(world.StateAt(2).Doors["fd-1"].State); // 穿越不是 LOG 確定的開關
        Assert.Equal(2, world.Final.Doors["fd-1"].LastCrossing!.Cause); // 開門後的移動不更新
        Assert.True(WorldModel.IsClosed(world.StateAt(2), "fd-1", DoorState.Closed));
        Assert.False(WorldModel.IsClosed(world.Final, "fd-1", DoorState.Closed)); // LOG 優先於平時狀態
    }

    [Fact]
    public void 快照推導與從頭推導相同()
    {
        var effects = new List<EventEffect> { Door(1, open: false) };
        for (int i = 0; i < 20; i++) effects.Add(Read(i + 2, i % 2 == 0 ? "A-01" : "A-02", $"P-{i % 3:000}"));
        var world = new WorldModel(effects, Layout(), "在席", snapshotEvery: 3);

        for (long seq = 0; seq <= 22; seq++)
            Assert.Equal(world.ReplayFromStart(seq).Fingerprint(), world.StateAt(seq).Fingerprint());
    }
}
