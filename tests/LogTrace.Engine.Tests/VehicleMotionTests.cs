using LogTrace.Engine.Parsing;
using LogTrace.Engine.Playback;
using LogTrace.Engine.Plants;

namespace LogTrace.Engine.Tests;

// 搬送機沿導軌移動（ADR-0005 第 10 點，#101）。LOG、編號與時間全部是虛構的。
public sealed class VehicleMotionTests
{
    static readonly DateTime T0 = new(2026, 1, 1, 8, 0, 0);

    /// <summary>直線導軌 y = 5，x 從 0 到 20；站點 A（x = 2）、B（x = 18）在導軌下方；料架排 01、列 1～10，x 從 0 到 20。</summary>
    static LayoutSection Layout(bool closed = false) => new()
    {
        StorageCodeRule = "RRCCCLL",
        Elements =
        [
            new() { Id = "rl-1", Type = LayoutElementType.Rail, Points = closed ? RailPath.LoopTemplate(0, 5) : RailPath.StraightTemplate(0, 5), Closed = closed ? true : null },
            new() { Id = "vh-1", Type = LayoutElementType.Vehicle, Aliases = ["V1"], X = 8, Y = 30, W = 2, H = 1, Rotation = 0, Rail = "rl-1" },
            new() { Id = "vh-2", Type = LayoutElementType.Vehicle, Aliases = ["V2"], X = 30, Y = 30, W = 2, H = 1, Rotation = 0 }, // 沒有導軌
            new() { Id = "st-a", Type = LayoutElementType.Station, Aliases = ["A"], X = 1, Y = 7, W = 2, H = 2, Rotation = 0 },
            new() { Id = "st-b", Type = LayoutElementType.Station, Aliases = ["B"], X = 17, Y = 7, W = 2, H = 2, Rotation = 0 },
            new() { Id = "rk-1", Type = LayoutElementType.Rack, Row = "01", ColFrom = 1, ColTo = 10, Levels = 3, X = 0, Y = 1, W = 20, H = 2, Rotation = 0 },
        ],
    };

    static EventEffect E(long seq, string effect, params (string Kind, string Value)[] p) =>
        new(seq, effect, p.ToDictionary(x => x.Kind, x => (IReadOnlyList<string>)[x.Value]));

    static EventEffect Dispatch(long seq, string dest, string? source = null, string vehicle = "V1", string command = "C-1") =>
        source is null
            ? E(seq, EffectKinds.SegmentDispatched, (FieldKinds.Command, command), (FieldKinds.Vehicle, vehicle), (FieldKinds.Destination, dest))
            : E(seq, EffectKinds.SegmentDispatched, (FieldKinds.Command, command), (FieldKinds.Vehicle, vehicle), (FieldKinds.Destination, dest), (FieldKinds.Source, source));

    static EventEffect Arrive(long seq, string dest, string command = "C-1") =>
        E(seq, EffectKinds.SegmentArrived, (FieldKinds.Command, command), (FieldKinds.Destination, dest));

    /// <summary>事件序號 n 的時間為 T0 + n × 10 秒。</summary>
    static VehicleMotion Motion(LayoutSection layout, params EventEffect[] effects) =>
        new(effects, layout, seq => T0.AddSeconds(seq * 10));

    [Fact]
    public void 第一次派送前在Layout的位置_沒有導軌的搬送機不移動()
    {
        var m = Motion(Layout(), Dispatch(2, "B", "A"), Arrive(4, "B"));

        var before = m.PlacementAt("vh-1", 1, T0.AddSeconds(10))!;
        Assert.Equal((9d, 30.5d, VehiclePositionBasis.Unknown), (before.X, before.Y, before.Basis));
        Assert.False(m.Moves("vh-2"));
        Assert.Null(m.PlacementAt("vh-2", 3, null));
    }

    [Fact]
    public void 派送到抵達之間依時間內插為推估_抵達後依LOG在目的地的投影()
    {
        var m = Motion(Layout(), Dispatch(2, "B", "A"), Arrive(4, "B")); // 20 秒到 40 秒，x 從 2 到 18

        var start = m.PlacementAt("vh-1", 2, T0.AddSeconds(20))!;
        Assert.Equal((2d, 5d, VehiclePositionBasis.Estimated, 2L), (Math.Round(start.X, 6), start.Y, start.Basis, start.DispatchCause!.Value));
        var mid = m.PlacementAt("vh-1", 3, T0.AddSeconds(30))!; // 一半
        Assert.Equal(10, mid.X, 6);
        Assert.True(mid.IsEstimated);
        var quarter = m.PlacementAt("vh-1", 2, T0.AddSeconds(25))!; // 兩筆事件之間（播放時）
        Assert.Equal(6, quarter.X, 6);

        var arrived = m.PlacementAt("vh-1", 4, T0.AddSeconds(40))!;
        Assert.Equal((18d, VehiclePositionBasis.Arrived, 4L), (Math.Round(arrived.X, 6), arrived.Basis, arrived.ArrivalCause!.Value));
        Assert.Equal(18, m.PlacementAt("vh-1", 99, T0.AddHours(1))!.X, 6); // 之後一直停在抵達處
    }

    [Fact]
    public void 沒有來源時起點用上一個位置_都沒有時直接畫在終點為推估()
    {
        var m = Motion(Layout(),
            Dispatch(2, "A"), Arrive(4, "A"),                         // 第一次沒有來源也沒有上一個位置
            Dispatch(6, "B", command: "C-2"), Arrive(8, "B", "C-2")); // 起點用上一個位置 A

        var first = m.PlacementAt("vh-1", 3, T0.AddSeconds(30))!;
        Assert.Equal((2d, VehiclePositionBasis.Estimated), (Math.Round(first.X, 6), first.Basis));
        var second = m.PlacementAt("vh-1", 7, T0.AddSeconds(70))!;
        Assert.Equal(10, second.X, 6);
    }

    [Fact]
    public void 來源和上一個位置不同時_派送那一刻跳到來源()
    {
        var m = Motion(Layout(), Dispatch(2, "A", "A"), Arrive(4, "A"), Dispatch(6, "A", "B", command: "C-2"), Arrive(8, "A", "C-2"));

        Assert.Equal(18, m.PlacementAt("vh-1", 6, T0.AddSeconds(60))!.X, 6);
    }

    [Fact]
    public void 儲位取那一列的中心投影到導軌()
    {
        // 料架寬 20 公尺、10 列，每列 2 公尺：列 3 在 x = 4～6，中心 x = 5（層不影響）
        var m = Motion(Layout(), Dispatch(2, "0100302", "A"), Arrive(4, "0100302"));

        Assert.Equal(5, m.PlacementAt("vh-1", 4, null)!.X, 6);
    }

    [Fact]
    public void 沒有抵達或取消時停在起點_作業完成不推導位置()
    {
        var never = Motion(Layout(), Dispatch(2, "B", "A"));
        var waiting = never.PlacementAt("vh-1", 50, T0.AddHours(1))!;
        Assert.Equal((2d, VehiclePositionBasis.WaitingArrival), (Math.Round(waiting.X, 6), waiting.Basis));

        var cancelled = Motion(Layout(), Dispatch(2, "B", "A"), E(4, EffectKinds.CommandCancelled, (FieldKinds.Command, "C-1")));
        Assert.Equal(VehiclePositionBasis.WaitingArrival, cancelled.PlacementAt("vh-1", 3, T0.AddSeconds(30))!.Basis); // 不內插
        var c = cancelled.PlacementAt("vh-1", 4, null)!;
        Assert.Equal((2d, VehiclePositionBasis.Cancelled), (Math.Round(c.X, 6), c.Basis));

        var finished = Motion(Layout(), Dispatch(2, "B", "A"), E(4, EffectKinds.SegmentFinished, (FieldKinds.Command, "C-1")));
        var f = finished.PlacementAt("vh-1", 4, null)!;
        Assert.Equal((2d, VehiclePositionBasis.FinishedNoLocation), (Math.Round(f.X, 6), f.Basis));
    }

    [Fact]
    public void 閉合導軌依點的順序方向前進()
    {
        // 跑道形環：上方直線 (0,5)→(20,5)，右半圓，下方 (20,11)→(0,11)，左半圓。從 B 投影（x = 18 上方）到 A（x = 2 上方）
        var m = Motion(Layout(closed: true), Dispatch(2, "A", "B"), Arrive(4, "A"));
        var rail = new RailPath(RailPath.LoopTemplate(0, 5), true);

        var mid = m.PlacementAt("vh-1", 3, T0.AddSeconds(30))!;
        // 依點的順序：從上方 x = 18 往右，繞過右半圓與下方直線，再繞左半圓回到上方 x = 2；一半的位置在下方直線上
        Assert.Equal(11, mid.Y, 6);
        var s = rail.Project(18, 8).Distance;
        var t = rail.Project(2, 8).Distance;
        var forward = ((t - s) % rail.Length + rail.Length) % rail.Length;
        Assert.Equal(rail.PointAt(s + forward / 2).X, mid.X, 6);
    }
}
