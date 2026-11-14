using LogTrace.Engine.Parsing;
using LogTrace.Engine.Playback;
using LogTrace.Engine.Plants;

namespace LogTrace.Engine.Tests;

// RM 與排的配置表（ADR-0019，#123）。LOG、編號與時間全部是虛構的。
public sealed class RmTests
{
    static readonly DateTime T0 = new(2026, 1, 1, 8, 0, 0);

    /// <summary>
    /// 巷道導軌 y = 5，x 從 0 到 20；RM 走這條導軌；Conv「P1」在導軌左端；
    /// 料架第 1 排（深）在導軌上方、第 2 排（淺）在導軌下方附近，列 1～10、每層 1.5 公尺（儲位規則 RCCCLL）。
    /// </summary>
    static LayoutSection Layout() => new()
    {
        StorageCodeRule = "RCCCLL",
        Elements =
        [
            new() { Id = "rl-1", Type = LayoutElementType.Rail, Points = RailPath.StraightTemplate(0, 5) },
            new() { Id = "rm-1", Type = LayoutElementType.Rm, Name = "RM 1", Aliases = ["RM01"], X = 8, Y = 4.4, W = 2, H = 1.2, Rotation = 0, Rail = "rl-1" },
            new() { Id = "vh-1", Type = LayoutElementType.Vehicle, Name = "STV 1", Aliases = ["STV01"], X = 8, Y = 20, W = 2, H = 1, Rotation = 0 },
            new() { Id = "eq-1", Type = LayoutElementType.Dock, Name = "Conv P1", Aliases = ["P1"], X = 0, Y = 6, W = 2, H = 2, Rotation = 0 },
            new() { Id = "rk-1", Type = LayoutElementType.Rack, Row = "1", ColFrom = 1, ColTo = 10, Levels = 4, LevelHeight = 1.5, X = 0, Y = 1, W = 20, H = 1.2, Rotation = 0 },
            new() { Id = "rk-2", Type = LayoutElementType.Rack, Row = "2", ColFrom = 1, ColTo = 10, Levels = 4, LevelHeight = 1.5, X = 0, Y = 2.4, W = 20, H = 1.2, Rotation = 0 },
        ],
        Rows = [new LayoutRow("1", "rm-1", RowDepth.Deep, "2"), new LayoutRow("2", "rm-1", RowDepth.Shallow, "1")],
    };

    static PlantConfigDocument Doc(LayoutSection layout) => new() { Plant = "範例廠", Revision = 1, Layout = layout };

    static EventEffect E(long seq, string effect, params (string Kind, string Value)[] p) =>
        new(seq, effect, p.ToDictionary(x => x.Kind, x => (IReadOnlyList<string>)[x.Value]));

    [Fact]
    public void 有RM或排的配置表時格式版本7_讀回來相同()
    {
        var json = PlantConfigFile.Serialize(Doc(Layout()));

        Assert.Contains("\"formatVersion\": 7", json);
        Assert.Contains("\"type\": \"rm\"", json);
        Assert.Contains("\"depth\": \"deep\"", json);
        var back = PlantConfigFile.Deserialize(json).Layout;
        Assert.Equal(Layout().Rows, back.Rows);
        Assert.Equal("rl-1", back.Elements.Single(e => e.Id == "rm-1").Rail);

        var plain = Layout();
        plain.Elements.RemoveAll(e => e.Type == LayoutElementType.Rm);
        plain.Rows = null;
        Assert.DoesNotContain("\"formatVersion\": 7", PlantConfigFile.Serialize(Doc(plain)));
        plain.Rows = [new LayoutRow("1")]; // 只有排的配置表（單深）也要版本 7
        Assert.Contains("\"formatVersion\": 7", PlantConfigFile.Serialize(Doc(plain)));
    }

    [Fact]
    public void 存檔前檢查_排號重複_RM不存在_配對不一致_雙深缺欄位_非搬送機指定導軌()
    {
        var layout = Layout();
        layout.Rows =
        [
            new LayoutRow("1", "rm-1", RowDepth.Deep, "2"),
            new LayoutRow("2", "rm-1", RowDepth.Deep, "1"),   // 兩個都是深
            new LayoutRow("3", "rm-9"),                        // RM 不存在
            new LayoutRow("4", "rm-1", RowDepth.Shallow),     // 缺配對
            new LayoutRow("4", "rm-1"),                        // 重複
        ];
        layout.Elements.Single(e => e.Id == "eq-1").Rail = "rl-1";

        var issues = PlantConfigFile.Validate(Doc(layout)).Select(i => i.Message).ToList();

        Assert.Contains(issues, m => m.Contains("一深一淺"));
        Assert.Contains(issues, m => m.Contains("「rm-9」不存在"));
        Assert.Contains(issues, m => m.Contains("同時填深淺與配對"));
        Assert.Contains(issues, m => m.Contains("排號「4」重複"));
        Assert.Contains(issues, m => m.Contains("只有搬送機與 RM 可以"));
        Assert.Empty(PlantConfigFile.Validate(Doc(Layout())));
    }

    [Fact]
    public void RM欄位的派送_貨物在RM上_RM執行中_搬送機欄位不會對應到RM()
    {
        var layout = Layout();
        var model = new WorldModel(
        [
            E(1, EffectKinds.CommandCreated, (FieldKinds.Command, "C-1"), (FieldKinds.Pallet, "P-001"), (FieldKinds.Source, "P1")),
            E(2, EffectKinds.SegmentDispatched, (FieldKinds.Command, "C-1"), (FieldKinds.Rm, "RM01"), (FieldKinds.Destination, "200302"), (FieldKinds.Source, "P1")),
            E(3, EffectKinds.SegmentArrived, (FieldKinds.Command, "C-1"), (FieldKinds.Destination, "200302")),
            E(4, EffectKinds.SegmentDispatched, (FieldKinds.Command, "C-2"), (FieldKinds.Vehicle, "RM01"), (FieldKinds.Destination, "P1")),
        ], layout, "在席");

        var s = model.StateAt(2);
        Assert.Equal((LocationKind.Vehicle, "rm-1"), (s.Pallets["P-001"].Location, s.Pallets["P-001"].LocationId));
        Assert.Equal(VehicleWork.Executing, s.Vehicles["rm-1"].Work);

        s = model.StateAt(3);
        Assert.Equal("P-001", s.Storage["200302"].Pallet); // 抵達淺庫位（第 2 排、003 列、02 層）
        Assert.Equal(VehicleWork.Idle, s.Vehicles["rm-1"].Work);

        s = model.StateAt(4);
        Assert.Equal(VehicleWork.Idle, s.Vehicles["rm-1"].Work); // 編號放在搬送機欄位：不是 RM
    }

    [Fact]
    public void RM沿導軌推估位置_載貨台高度依儲位的層()
    {
        var motion = new VehicleMotion(
        [
            E(1, EffectKinds.SegmentDispatched, (FieldKinds.Command, "C-1"), (FieldKinds.Rm, "RM01"), (FieldKinds.Destination, "200304"), (FieldKinds.Source, "P1")),
            E(3, EffectKinds.SegmentArrived, (FieldKinds.Command, "C-1"), (FieldKinds.Destination, "200304")),
        ], Layout(), seq => T0.AddSeconds(seq * 10));

        var arrived = motion.PlacementAt("rm-1", 3, null)!;
        Assert.Equal(VehiclePositionBasis.Arrived, arrived.Basis);
        Assert.Equal(5, arrived.X, 6); // 第 3 列的中心 x = 5
        Assert.Equal(4.5, arrived.Lift!.Value, 6); // 第 4 層：(4 − 1) × 1.5

        var mid = motion.PlacementAt("rm-1", 1, T0.AddSeconds(20))!; // 派送 10 秒、抵達 30 秒：一半
        Assert.True(mid.IsEstimated);
        Assert.Equal(2.25, mid.Lift!.Value, 6); // 從 Conv（0）到 4.5 的一半
        Assert.Null(motion.PlacementAt("vh-1", 1, null)); // 沒有導軌的搬送機不移動
    }
}
