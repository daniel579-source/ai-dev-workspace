using LogTrace.Engine.Parsing;
using LogTrace.Engine.Playback;
using LogTrace.Engine.Plants;

namespace LogTrace.Engine.Tests;

// 一段作業完成但 LOG 沒有記錄地點（#53，ADR-0005 第 6 點）：不推導地點，另外標示狀態。資料為虛構。
public sealed class SegmentFinishedTests
{
    static readonly LayoutSection Layout = new()
    {
        StorageCodeRule = "RRCCCLL",
        Elements =
        [
            new() { Id = "vh-1", Type = LayoutElementType.Vehicle, Name = "SRCA01", Aliases = ["SRCA01"] },
            new() { Id = "st-1", Type = LayoutElementType.Station, Name = "入庫口1", Aliases = ["A-01"] },
        ],
    };

    static EventEffect E(long seq, string effect, params (string Kind, string Value)[] p) =>
        new(seq, effect, p.ToDictionary(x => x.Kind, x => (IReadOnlyList<string>)[x.Value]));

    static readonly EventEffect[] Effects =
    [
        E(1, EffectKinds.PalletRead, (FieldKinds.Station, "A-01"), (FieldKinds.Pallet, "PLT-X")),
        E(2, EffectKinds.CommandCreated, (FieldKinds.Command, "00090001"), (FieldKinds.Pallet, "PLT-X"), (FieldKinds.Source, "A-01")),
        E(3, EffectKinds.SegmentDispatched, (FieldKinds.Command, "00090001"), (FieldKinds.Vehicle, "SRCA01"), (FieldKinds.Destination, "0100101")),
        E(4, EffectKinds.SegmentFinished, (FieldKinds.Command, "00090001"), (FieldKinds.Vehicle, "SRCA01")),
        E(5, EffectKinds.CommandCompleted, (FieldKinds.Command, "00090001")),
    ];

    [Fact]
    public void 一段作業完成_狀態另外標示_搬送機回到閒置_目的地是儲位時放入()
    {
        var world = new WorldModel(Effects, Layout, "在席");

        var w = world.StateAt(4);
        var c = w.Commands["00090001"];
        Assert.Equal((CommandStatus.SegmentFinished, 1, 4L), (c.Status, c.Segment, c.StatusCause));
        Assert.Equal(VehicleWork.Idle, w.Vehicles["vh-1"].Work);
        Assert.Null(w.Vehicles["vh-1"].Command);
        // 派送的目的地是儲位：作業完成視為放入（ADR-0005 第 2 點 2026-11-04 補充，#95）；其他目的地見 StorageStateTests
        Assert.Equal((LocationKind.Storage, "0100101"), (w.Pallets["PLT-X"].Location, w.Pallets["PLT-X"].LocationId));
        Assert.Equal(("PLT-X", 4L), (w.Storage["0100101"].Pallet, w.Storage["0100101"].Cause));
        Assert.False(c.IsFinished); // 一段作業完成不等於命令完成

        Assert.Equal(CommandStatus.Completed, world.Final.Commands["00090001"].Status);
    }

    [Fact]
    public void 一段作業完成_沒有搬送機時_以這一段派送的搬送機回到閒置()
    {
        var world = new WorldModel(
        [
            Effects[2],
            E(4, EffectKinds.SegmentFinished, (FieldKinds.Command, "00090001")),
        ], Layout, "在席");

        Assert.Equal(VehicleWork.Idle, world.Final.Vehicles["vh-1"].Work);
        Assert.Equal(CommandStatus.SegmentFinished, world.Final.Commands["00090001"].Status);
    }

    [Fact]
    public void 一段作業完成的效果規則只需要命令編號_抵達仍需要目的地()
    {
        Assert.Null(FieldExtractor.Check(new ParseRule("作業完成（虛構）", null, null, @"作業完成 (?<command>[0-9]{8})", EffectKinds.SegmentFinished)));
        Assert.Contains("需要欄位", FieldExtractor.Check(new ParseRule("抵達（虛構）", null, null, @"抵達 (?<command>[0-9]{8})", EffectKinds.SegmentArrived)));
    }
}
