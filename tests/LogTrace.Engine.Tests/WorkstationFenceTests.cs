using LogTrace.Engine.Parsing;
using LogTrace.Engine.Playback;
using LogTrace.Engine.Plants;

namespace LogTrace.Engine.Tests;

// 工站點、圍欄、圍欄門與電梯閘門（ADR-0020）。編號與配置全部是虛構的。
public sealed class WorkstationFenceTests
{
    static LayoutElement Ws(string id, string alias, double x, double y) =>
        new() { Id = id, Type = LayoutElementType.Workstation, Name = id, Aliases = [alias], X = x, Y = y, W = 2, H = 2, Rotation = 0 };

    static LayoutElement Fence(string id, params (double X, double Y)[] points) =>
        new() { Id = id, Type = LayoutElementType.Fence, Name = id, Points = [.. points.Select(p => new RailPoint(p.X, p.Y))] };

    [Fact]
    public void 用到工站點_圍欄或圍欄門時格式版本為8_讀寫往返_沒用到時照舊()
    {
        var doc = new PlantConfigDocument { Plant = "虛構廠" };
        Assert.True(doc.RequiredFormatVersion() < 8);

        doc.Layout.Elements = [Ws("ws-1", "W-01", 0, 0), Fence("fc-1", (0, 5), (10, 5)),
            new() { Id = "fg-1", Type = LayoutElementType.FenceGate, Name = "門", X = 10, Y = 5, W = 1, H = 0.1, Rotation = 0 }];
        Assert.Equal(8, doc.RequiredFormatVersion());
        Assert.Empty(PlantConfigFile.Validate(doc));

        var back = PlantConfigFile.Deserialize(PlantConfigFile.Serialize(doc));
        Assert.Equal([LayoutElementType.Workstation, LayoutElementType.Fence, LayoutElementType.FenceGate], back.Layout.Elements.Select(e => e.Type));
        Assert.Equal(2, back.Layout.Elements[1].Points!.Count);
        Assert.Contains("\"type\": \"fenceGate\"", PlantConfigFile.Serialize(doc));
    }

    [Fact]
    public void 圍欄至少要有兩個點_工站點可以有別名_可以當輸送線的一端()
    {
        var doc = new PlantConfigDocument { Plant = "虛構廠" };
        doc.Layout.Elements = [Fence("fc-1", (0, 0)), Ws("ws-1", "W-01", 0, 0),
            new() { Id = "st-1", Type = LayoutElementType.Station, Name = "st", Aliases = ["A-01"], X = 4, Y = 0, W = 2, H = 2, Rotation = 0 },
            new() { Id = "cv-1", Type = LayoutElementType.Conveyor, From = "ws-1", To = "st-1" }];

        var issues = PlantConfigFile.Validate(doc);

        Assert.Contains(issues, i => i.Message.Contains("圍欄「fc-1」至少要有 2 個點"));
        Assert.Single(issues);
        Assert.True(LayoutElement.CanHaveAliases(LayoutElementType.Workstation));
        Assert.False(LayoutElement.CanHaveAliases(LayoutElementType.Fence));
        Assert.False(LayoutElement.CanHaveAliases(LayoutElementType.FenceGate));
    }

    [Fact]
    public void 工站點的狀態推導比照站點_讀到棧板_在席_不一致提示()
    {
        var layout = new LayoutSection { Elements = [Ws("ws-1", "W-01", 0, 0)] };
        static EventEffect E(long seq, string effect, params (string Kind, string Value)[] p) =>
            new(seq, effect, p.ToDictionary(x => x.Kind, x => (IReadOnlyList<string>)[x.Value]));
        var model = new WorldModel(
        [
            E(1, EffectKinds.Signal, (FieldKinds.Station, "W-01"), (FieldKinds.Signal, "在席"), (FieldKinds.SignalValue, "OFF")),
            E(2, EffectKinds.PalletRead, (FieldKinds.Station, "W-01"), (FieldKinds.Pallet, "P-001")),
            E(3, EffectKinds.StationStatus, (FieldKinds.Station, "W-01"), (FieldKinds.StationStatus, "手動")),
        ], layout, "在席");

        var w = model.StateAt(3);

        Assert.Equal((LocationKind.Station, "ws-1"), (w.Pallets["P-001"].Location, w.Pallets["P-001"].LocationId));
        Assert.False(w.Stations["ws-1"].Presence);
        Assert.Equal("手動", w.Stations["ws-1"].Status);
        Assert.True(w.IsInconsistent("ws-1"));
    }

    static LayoutSection ElevatorLayout(params LayoutElement[] others) => new()
    {
        Floors = ["1F", "2F"],
        Elements =
        [
            new() { Id = "ev-1", Type = LayoutElementType.Elevator, Name = "電梯", Aliases = ["EV01"], X = 10, Y = 10, W = 2, H = 2, Rotation = 0, ServedFloors = ["1F", "2F"] },
            .. others,
        ],
    };

    [Fact]
    public void 電梯閘門_緊鄰的Conv站點工站點那一側有閘門_兩側都對接時兩側都有_不在服務樓層或離太遠的不算()
    {
        var layout = ElevatorLayout(
            new() { Id = "eq-1", Type = LayoutElementType.Dock, Name = "左", Aliases = ["EQ1"], X = 8, Y = 10, W = 2, H = 2, Rotation = 0, Floor = "1F" },
            Ws("ws-1", "W-01", 12.2, 10),                                                                                    // 右側，相距 0.2 公尺
            new() { Id = "st-9", Type = LayoutElementType.Station, Name = "遠", Aliases = ["A-09"], X = 10, Y = 14, W = 2, H = 2, Rotation = 0 }, // 下方 2 公尺
            new() { Id = "st-3", Type = LayoutElementType.Station, Name = "3F", Aliases = ["A-03"], X = 10, Y = 7.9, W = 2, H = 2, Rotation = 0, Floor = "3F" });

        Assert.Equal([ElevatorGates.XMin, ElevatorGates.XMax], ElevatorGates.SidesOf(layout.Elements[0], layout));
    }

    [Fact]
    public void 電梯旋轉90度時_側面以電梯自己的座標表示()
    {
        var layout = ElevatorLayout(new LayoutElement { Id = "st-1", Type = LayoutElementType.Station, Name = "上", Aliases = ["A-01"], X = 10, Y = 8, W = 2, H = 2, Rotation = 0 });
        layout.Elements[0].Rotation = 90;

        // 旋轉 90 度後，樓層座標的上方（y 較小）是電梯自己的 -x 方向
        Assert.Equal([ElevatorGates.XMin], ElevatorGates.SidesOf(layout.Elements[0], layout));
        Assert.Equal(ElevatorGates.XMax, ElevatorGates.SideFacing(layout.Elements[0], 11, 14));
    }
}
