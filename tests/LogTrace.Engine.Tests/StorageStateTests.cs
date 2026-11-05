using LogTrace.Engine.Parsing;
using LogTrace.Engine.Playback;
using LogTrace.Engine.Plants;

namespace LogTrace.Engine.Tests;

// 儲位的三種狀態：未取得資訊、有貨、空，一律以 LOG 記載為準（ADR-0005 第 2 點 2026-11-04 補充，#95）。資料為虛構。
public sealed class StorageStateTests
{
    static readonly LayoutSection Layout = new()
    {
        StorageCodeRule = "RRCCCLL",
        Elements =
        [
            new() { Id = "vh-1", Type = LayoutElementType.Vehicle, Name = "SRCA01", Aliases = ["SRCA01"] },
            new() { Id = "st-1", Type = LayoutElementType.Station, Name = "入庫口1", Aliases = ["A-01"] },
            new() { Id = "st-2", Type = LayoutElementType.Station, Name = "出庫口1", Aliases = ["B-01"] },
        ],
    };

    static EventEffect E(long seq, string effect, params (string Kind, string Value)[] p) =>
        new(seq, effect, p.ToDictionary(x => x.Kind, x => (IReadOnlyList<string>)[x.Value]));

    static WorldModel World(params EventEffect[] effects) => new(effects, Layout, "在席", snapshotEvery: 2);

    static EventEffect Created(long seq, string command, string? pallet) => pallet is null
        ? E(seq, EffectKinds.CommandCreated, (FieldKinds.Command, command))
        : E(seq, EffectKinds.CommandCreated, (FieldKinds.Command, command), (FieldKinds.Pallet, pallet));

    static EventEffect Dispatch(long seq, string command, string destination, string? source = null) => source is null
        ? E(seq, EffectKinds.SegmentDispatched, (FieldKinds.Command, command), (FieldKinds.Vehicle, "SRCA01"), (FieldKinds.Destination, destination))
        : E(seq, EffectKinds.SegmentDispatched, (FieldKinds.Command, command), (FieldKinds.Vehicle, "SRCA01"), (FieldKinds.Destination, destination), (FieldKinds.Source, source));

    static EventEffect Finished(long seq, string command) => E(seq, EffectKinds.SegmentFinished, (FieldKinds.Command, command), (FieldKinds.Vehicle, "SRCA01"));

    static EventEffect Arrived(long seq, string command, string destination) => E(seq, EffectKinds.SegmentArrived, (FieldKinds.Command, command), (FieldKinds.Destination, destination));

    [Fact]
    public void 還沒有相關LOG的儲位_是未取得資訊()
    {
        var world = World(E(1, EffectKinds.PalletRead, (FieldKinds.Station, "A-01"), (FieldKinds.Pallet, "PLT-1")), Created(2, "C1", "PLT-1"));

        Assert.Empty(world.Final.Storage);
    }

    [Fact]
    public void 派送目的地是儲位_作業完成時放入()
    {
        var world = World(
            E(1, EffectKinds.PalletRead, (FieldKinds.Station, "A-01"), (FieldKinds.Pallet, "PLT-1")),
            Created(2, "C1", "PLT-1"),
            Dispatch(3, "C1", "0100302"),
            Finished(4, "C1"));

        Assert.Empty(world.StateAt(3).Storage); // 派送中：還沒放入
        var w = world.StateAt(4);
        Assert.Equal(new StorageSlotState("PLT-1", 4), w.Storage["0100302"]);
        Assert.Equal((LocationKind.Storage, "0100302", 4L), (w.Pallets["PLT-1"].Location, w.Pallets["PLT-1"].LocationId, w.Pallets["PLT-1"].LocationCause));
    }

    [Fact]
    public void 派送目的地是儲位_抵達時放入()
    {
        var world = World(
            E(1, EffectKinds.PalletRead, (FieldKinds.Station, "A-01"), (FieldKinds.Pallet, "PLT-1")),
            Created(2, "C1", "PLT-1"),
            Dispatch(3, "C1", "0100302"),
            Arrived(4, "C1", "0100302"));

        Assert.Equal(new StorageSlotState("PLT-1", 4), world.Final.Storage["0100302"]);
    }

    [Fact]
    public void 目的地不是儲位_作業完成仍不推導貨物位置()
    {
        var world = World(
            E(1, EffectKinds.PalletRead, (FieldKinds.Station, "A-01"), (FieldKinds.Pallet, "PLT-1")),
            Created(2, "C1", "PLT-1"),
            Dispatch(3, "C1", "B-01"),
            Finished(4, "C1"));

        var p = world.Final.Pallets["PLT-1"];
        Assert.Equal((LocationKind.Vehicle, "vh-1"), (p.Location, p.LocationId)); // #53
        Assert.Empty(world.Final.Storage);
    }

    [Fact]
    public void 棧板之前沒有位置紀錄_作業完成時建立並放入儲位()
    {
        var world = World(Created(1, "C1", "PLT-1"), Dispatch(2, "C1", "0100302"), Finished(3, "C1"));

        var p = world.Final.Pallets["PLT-1"];
        Assert.Equal((LocationKind.Storage, "0100302"), (p.Location, p.LocationId));
        Assert.False(p.Preexisting);
        Assert.Equal("PLT-1", world.Final.Storage["0100302"].Pallet);
    }

    [Fact]
    public void 派送來源是儲位_派送時棧板在搬送機上_儲位變空()
    {
        var world = World(Created(1, "C2", "PLT-2"), Dispatch(2, "C2", "B-01", source: "0100501"));

        var w = world.Final;
        var p = w.Pallets["PLT-2"];
        Assert.True(p.Preexisting); // 第一次提到就在儲位：案件開始前就在那裡
        Assert.Equal((LocationKind.Vehicle, "vh-1"), (p.Location, p.LocationId));
        Assert.Equal(new StorageSlotState(null, 2), w.Storage["0100501"]);
        Assert.True(w.Storage["0100501"].IsEmpty);
    }

    [Fact]
    public void 派送來源是儲位_命令沒有棧板編號_儲位仍變空()
    {
        var world = World(Created(1, "C2", null), Dispatch(2, "C2", "B-01", source: "0100501"));

        Assert.True(world.Final.Storage["0100501"].IsEmpty);
        Assert.Empty(world.Final.Pallets);
    }

    [Fact]
    public void 派送來源的儲位記錄著另一個棧板_那個棧板的位置改為不明()
    {
        var world = World(
            Created(1, "C1", "PLT-9"), Dispatch(2, "C1", "0100501"), Finished(3, "C1"), // PLT-9 放進 0100501
            Created(4, "C2", "PLT-2"), Dispatch(5, "C2", "B-01", source: "0100501"));

        var w = world.Final;
        Assert.True(w.Storage["0100501"].IsEmpty);
        Assert.Equal((LocationKind.Unknown, (string?)null, 5L), (w.Pallets["PLT-9"].Location, w.Pallets["PLT-9"].LocationId, w.Pallets["PLT-9"].LocationCause));
        Assert.Equal(LocationKind.Vehicle, w.Pallets["PLT-2"].Location);
    }

    [Fact]
    public void 放入_取出_再放入_儲位依LOG照時間變化()
    {
        var world = World(
            Created(1, "C1", "PLT-1"), Dispatch(2, "C1", "0100302"), Finished(3, "C1"),
            Created(4, "C2", "PLT-1"), Dispatch(5, "C2", "B-01", source: "0100302"), Arrived(6, "C2", "B-01"),
            Created(7, "C3", "PLT-3"), Dispatch(8, "C3", "0100302"), Finished(9, "C3"));

        Assert.False(world.StateAt(2).Storage.ContainsKey("0100302"));
        Assert.Equal("PLT-1", world.StateAt(3).Storage["0100302"].Pallet);
        Assert.Equal("PLT-1", world.StateAt(4).Storage["0100302"].Pallet);
        Assert.True(world.StateAt(5).Storage["0100302"].IsEmpty);
        Assert.Equal((LocationKind.Station, "st-2"), (world.StateAt(6).Pallets["PLT-1"].Location, world.StateAt(6).Pallets["PLT-1"].LocationId));
        Assert.True(world.StateAt(8).Storage["0100302"].IsEmpty);
        Assert.Equal(new StorageSlotState("PLT-3", 9), world.StateAt(9).Storage["0100302"]);
    }

    [Fact]
    public void 任意時刻的儲位狀態_與從頭推導相同()
    {
        var world = World(
            Created(1, "C1", "PLT-1"), Dispatch(2, "C1", "0100302"), Finished(3, "C1"),
            Created(4, "C2", "PLT-1"), Dispatch(5, "C2", "B-01", source: "0100302"), Arrived(6, "C2", "B-01"),
            Created(7, "C3", null), Dispatch(8, "C3", "B-01", source: "0100501"),
            Created(9, "C4", "PLT-4"), Dispatch(10, "C4", "0100501"), Arrived(11, "C4", "0100501"));

        foreach (var seq in new long[] { 11, 0, 5, 3, 8, 1, 10, 6, 2, 9, 4, 7 }) // 往前、往後跳
            Assert.Equal(world.ReplayFromStart(seq).Fingerprint(), world.StateAt(seq).Fingerprint());
    }
}
