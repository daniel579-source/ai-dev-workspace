using LogTrace.Engine.Parsing;
using LogTrace.Engine.Playback;
using LogTrace.Engine.Plants;

namespace LogTrace.Engine.Tests;

// 命令的取消與改派（ADR-0005 第 6 點：LOG 樣式待確認，以可設定的規則與虛構資料處理）
public sealed class CommandCancelTests
{
    static readonly LayoutSection Layout = new()
    {
        StorageCodeRule = "RRCCCLL",
        Elements =
        [
            new() { Id = "vh-1", Type = LayoutElementType.Vehicle, Name = "SRCA01", Aliases = ["SRCA01"] },
            new() { Id = "vh-2", Type = LayoutElementType.Vehicle, Name = "SRCA02", Aliases = ["SRCA02"] },
            new() { Id = "st-1", Type = LayoutElementType.Station, Name = "入庫口1", Aliases = ["A-01"] },
        ],
    };

    static EventEffect E(long seq, string effect, params (string Kind, string Value)[] p) =>
        new(seq, effect, p.ToDictionary(x => x.Kind, x => (IReadOnlyList<string>)[x.Value]));

    [Fact]
    public void 命令取消_狀態為已取消_執行中的搬送機回到閒置_不推測貨物位置()
    {
        var world = new WorldModel(
        [
            E(1, EffectKinds.PalletRead, (FieldKinds.Station, "A-01"), (FieldKinds.Pallet, "PLT-X")),
            E(2, EffectKinds.CommandCreated, (FieldKinds.Command, "00090001"), (FieldKinds.Pallet, "PLT-X"), (FieldKinds.Source, "A-01")),
            E(3, EffectKinds.SegmentDispatched, (FieldKinds.Command, "00090001"), (FieldKinds.Vehicle, "SRCA01"), (FieldKinds.Destination, "0100101")),
            E(4, EffectKinds.CommandCancelled, (FieldKinds.Command, "00090001")),
        ], Layout, "在席");

        var w = world.Final;
        Assert.Equal(CommandStatus.Cancelled, w.Commands["00090001"].Status);
        Assert.Equal(4, w.Commands["00090001"].StatusCause);
        Assert.Equal(VehicleWork.Idle, w.Vehicles["vh-1"].Work);
        Assert.Equal((LocationKind.Vehicle, "vh-1"), (w.Pallets["PLT-X"].Location, w.Pallets["PLT-X"].LocationId)); // 不猜測
        Assert.False(world.IsUnfinishedInCase("00090001")); // 已取消不算「案件範圍內未完成」
    }

    [Fact]
    public void 命令改派_同一段再派給另一台搬送機_原本的搬送機不受影響()
    {
        var world = new WorldModel(
        [
            E(1, EffectKinds.SegmentDispatched, (FieldKinds.Command, "00090002"), (FieldKinds.Segment, "1"), (FieldKinds.Vehicle, "SRCA01"), (FieldKinds.Destination, "A-01")),
            E(2, EffectKinds.SegmentDispatched, (FieldKinds.Command, "00090002"), (FieldKinds.Segment, "1"), (FieldKinds.Vehicle, "SRCA02"), (FieldKinds.Destination, "A-01")),
        ], Layout, "在席");

        var c = world.Final.Commands["00090002"];
        Assert.Equal(("vh-2", 1), (c.Vehicle, c.Segment));
        Assert.Equal("00090002", world.Final.Vehicles["vh-2"].Command);
        // 改派的 LOG 樣式待確認：原本那台的作業不推測，維持最後的紀錄
        Assert.Equal(VehicleWork.Executing, world.Final.Vehicles["vh-1"].Work);
    }

    [Fact]
    public void 取消的效果規則需要命令編號()
    {
        Assert.Contains("需要欄位", FieldExtractor.Check(new ParseRule("取消（虛構）", null, null, @"取消 (?<station>A-[0-9]{2})", EffectKinds.CommandCancelled)));
        Assert.Null(FieldExtractor.Check(new ParseRule("取消（虛構）", null, null, @"取消命令 (?<command>[0-9]{8})", EffectKinds.CommandCancelled)));
    }
}
