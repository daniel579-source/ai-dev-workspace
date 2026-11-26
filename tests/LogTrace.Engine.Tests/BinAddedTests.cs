using LogTrace.Engine.Parsing;
using LogTrace.Engine.Playback;
using LogTrace.Engine.Plants;

namespace LogTrace.Engine.Tests;

// 棧板加入一個桶（ADR-0005 第 2 點 2026-11-15 補充）。編號全部是虛構的。
public sealed class BinAddedTests
{
    static readonly LayoutSection Layout = new()
    {
        Elements = [new() { Id = "st-1", Type = LayoutElementType.Conv, Aliases = ["A-01"], X = 1, Y = 1, W = 2, H = 2, Rotation = 0 }],
    };

    static EventEffect E(long seq, string effect, params (string Kind, string Value)[] p) =>
        new(seq, effect, p.GroupBy(x => x.Kind).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Value).ToList()));

    [Fact]
    public void 每行一個桶_累加到棧板的桶清單_重複的不再加入_不移動棧板()
    {
        var model = new WorldModel(
        [
            E(1, EffectKinds.PalletRead, (FieldKinds.Conv, "A-01"), (FieldKinds.Pallet, "P-001")),
            E(2, EffectKinds.BinAdded, (FieldKinds.Pallet, "P-001"), (FieldKinds.Bin, "B-01")),
            E(3, EffectKinds.BinAdded, (FieldKinds.Pallet, "P-001"), (FieldKinds.Bin, "B-02")),
            E(4, EffectKinds.BinAdded, (FieldKinds.Pallet, "P-001"), (FieldKinds.Bin, "B-01")), // 重複
        ], Layout, "在席");

        var s = model.StateAt(4);
        Assert.Equal(["B-01", "B-02"], s.Pallets["P-001"].Bins);
        Assert.Equal((LocationKind.Station, "st-1"), (s.Pallets["P-001"].Location, s.Pallets["P-001"].LocationId)); // 位置不變
        Assert.Equal(["B-01"], model.StateAt(2).Pallets["P-001"].Bins); // 之前的時刻不受之後的事件影響
    }

    [Fact]
    public void 棧板還沒有紀錄時先建立_位置未知()
    {
        var model = new WorldModel([E(1, EffectKinds.BinAdded, (FieldKinds.Pallet, "P-009"), (FieldKinds.Bin, "B-09"))], Layout, "在席");

        var p = model.StateAt(1).Pallets["P-009"];
        Assert.Equal(LocationKind.Unknown, p.Location);
        Assert.Equal(["B-09"], p.Bins);
    }

    [Fact]
    public void 效果需要棧板與桶兩個欄位()
    {
        var required = EffectKinds.All.Single(e => e.Effect == EffectKinds.BinAdded).Required;
        Assert.Equal([FieldKinds.Pallet, FieldKinds.Bin], required);
    }
}
