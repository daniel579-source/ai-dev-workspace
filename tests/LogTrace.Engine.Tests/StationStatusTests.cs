using LogTrace.Engine.Parsing;
using LogTrace.Engine.Playback;
using LogTrace.Engine.Plants;

namespace LogTrace.Engine.Tests;

// 站點的運轉狀態（ADR-0005 第 3、8 點，#70）。LOG 與編號全部是虛構的。
public sealed class StationStatusTests
{
    static readonly LayoutSection Layout = new()
    {
        Elements = [new() { Id = "st-1", Type = LayoutElementType.Station, Name = "入庫口1", Aliases = ["A-01"] }],
    };

    static EventEffect E(long seq, string effect, params (string Kind, string Value)[] p) =>
        new(seq, effect, p.ToDictionary(x => x.Kind, x => (IReadOnlyList<string>)[x.Value]));

    [Fact]
    public void 規則抽出站點與運轉狀態_照LOG的文字()
    {
        var rule = new ParseRule("站點運轉狀態（虛構）", "PLC", null, @"^\[(?<station>[A-Z]-[0-9]{2})\]\[狀態\] => (?<stationStatus>\S+)\s*$", EffectKinds.StationStatus);
        Assert.Null(FieldExtractor.Check(rule));

        var (fields, effects) = new FieldExtractor([rule]).ExtractWithEffects(7, "PLC", "PLCLOG", "[A-01][狀態] => 半自動");

        Assert.Contains(new EventField(7, FieldKinds.StationStatus, "半自動"), fields); // 不限定種類
        Assert.Equal(EffectKinds.StationStatus, Assert.Single(effects).Effect);
        Assert.Equal("運轉狀態", FieldKinds.Label(FieldKinds.StationStatus));
    }

    [Fact]
    public void 效果規則需要站點與運轉狀態()
    {
        Assert.Contains("需要欄位", FieldExtractor.Check(new ParseRule("缺站點（虛構）", null, null, @"狀態 (?<stationStatus>\S+)", EffectKinds.StationStatus)));
    }

    [Fact]
    public void 運轉狀態與站點模式分開_第一次切換前未知_異常不影響警報()
    {
        var world = new WorldModel(
        [
            E(1, EffectKinds.StationMode, (FieldKinds.Station, "A-01"), (FieldKinds.StationMode, "1")),
            E(2, EffectKinds.StationStatus, (FieldKinds.Station, "A-01"), (FieldKinds.StationStatus, "手動")),
            E(3, EffectKinds.StationStatus, (FieldKinds.Station, "A-01"), (FieldKinds.StationStatus, "異常")),
            E(4, EffectKinds.StationStatus, (FieldKinds.Station, "A-99"), (FieldKinds.StationStatus, "手動")), // 對應不到 Layout
        ], Layout, "在席");

        Assert.Null(world.StateAt(1).Stations["st-1"].Status);
        var s2 = world.StateAt(2).Stations["st-1"];
        Assert.Equal(("1", "手動", 2L), (s2.Mode, s2.Status, s2.StatusCause));
        Assert.Equal(("異常", 3L), (world.Final.Stations["st-1"].Status, world.Final.Stations["st-1"].StatusCause));
        Assert.Empty(world.Final.Alarms); // 異常中只由警報決定
        Assert.Single(world.Final.Stations);
    }
}
