using LogTrace.Engine.Parsing;
using LogTrace.Engine.Playback;
using LogTrace.Engine.Plants;

namespace LogTrace.Engine.Tests;

// Conv 搭配導軌移動（ADR-0028）：位置依貨物推估。LOG、編號、時間與座標全部是虛構的。
public sealed class ConvMotionTests
{
    static readonly DateTime T0 = new(2030, 1, 9, 10, 0, 0);

    /// <summary>
    /// 導軌 rl-1 從 (1,1) 到 (21,1)；搭配導軌的 Conv st-1（C1）擺在 (0,0) 2×2（中心 (1,1)＝導軌 0 公尺）；
    /// Conv st-2（A）中心 (6,4)＝導軌 5 公尺；搬送機 vh-1（V1）中心 (15,4.5)＝導軌 14 公尺；Conv st-3（Z）在 2F。
    /// </summary>
    static LayoutSection Layout() => new()
    {
        Floors = ["1F", "2F"],
        Elements =
        [
            new() { Id = "rl-1", Type = LayoutElementType.Rail, Floor = "1F", Points = [new(1, 1), new(21, 1)] },
            new() { Id = "st-1", Type = LayoutElementType.Conv, Name = "移動 Conv", Floor = "1F", Aliases = ["C1"], Rail = "rl-1", X = 0, Y = 0, W = 2, H = 2, Rotation = 0 },
            new() { Id = "st-2", Type = LayoutElementType.Conv, Name = "Conv A", Floor = "1F", Aliases = ["A"], X = 5, Y = 3, W = 2, H = 2, Rotation = 0 },
            new() { Id = "vh-1", Type = LayoutElementType.Vehicle, Name = "搬送機 1", Floor = "1F", Aliases = ["V1"], X = 14, Y = 4, W = 2, H = 1, Rotation = 0 },
            new() { Id = "st-3", Type = LayoutElementType.Conv, Name = "Conv Z", Floor = "2F", Aliases = ["Z"], X = 10, Y = 3, W = 2, H = 2, Rotation = 0 },
        ],
    };

    static EventEffect E(long seq, string effect, params (string Kind, string Value)[] p) =>
        new(seq, effect, p.ToDictionary(x => x.Kind, x => (IReadOnlyList<string>)[x.Value]));

    // 事件序號 → 秒
    static readonly Dictionary<long, int> Seconds = new() { [1] = 0, [2] = 10, [3] = 20, [4] = 40, [5] = 50, [6] = 60, [7] = 70 };

    static (WorldModel Model, ConvMotion Motion) Build(params EventEffect[] effects)
    {
        var m = new WorldModel(effects, Layout(), "在席");
        return (m, new ConvMotion(m.PalletMoves, Layout(), s => Seconds.TryGetValue(s, out var sec) ? T0.AddSeconds(sec) : null));
    }

    static readonly EventEffect[] Sample =
    [
        E(1, EffectKinds.PalletRead, (FieldKinds.Conv, "A"), (FieldKinds.Pallet, "P-001")),
        E(2, EffectKinds.PalletRead, (FieldKinds.Conv, "C1"), (FieldKinds.Pallet, "P-001")), // 從 A 進入：對齊 A（5 公尺）
        E(3, EffectKinds.CommandCreated, (FieldKinds.Command, "C-001"), (FieldKinds.Pallet, "P-001")),
        E(4, EffectKinds.SegmentDispatched, (FieldKinds.Command, "C-001"), (FieldKinds.Vehicle, "V1"), (FieldKinds.Destination, "A")), // 被 V1 取走：對齊 V1（14 公尺）
    ];

    [Fact]
    public void 還沒有對齊前在擺放的位置_貨物從A進入時直接對齊A()
    {
        var (_, motion) = Build(Sample);

        var before = motion.PlacementAt("st-1", 1, T0);
        Assert.Equal((1.0, 1.0, false, (long?)null), (before!.X, before.Y, before.IsEstimated, before.Cause));
        var at = motion.PlacementAt("st-1", 2, T0.AddSeconds(10))!;
        Assert.Equal((6.0, 1.0), (at.X, at.Y));
        Assert.True(at.IsEstimated);
        Assert.Equal(("st-2", true, 2L), (at.Target, at.Entering, at.Cause));
    }

    [Fact]
    public void 兩次對齊之間依時間內插_離開時對齊取走它的搬送機()
    {
        var (_, motion) = Build(Sample);

        var half = motion.PlacementAt("st-1", 3, T0.AddSeconds(25))!; // 10 秒（5 公尺）到 40 秒（14 公尺）的一半
        Assert.True(half.Moving);
        Assert.Equal(1 + 9.5, half.X, 6);
        Assert.Equal("vh-1", half.Target);
        var end = motion.PlacementAt("st-1", 4, T0.AddSeconds(40))!;
        Assert.False(end.Moving);
        Assert.Equal((15.0, false), (end.X, end.Entering));
    }

    [Fact]
    public void 前一個位置不明或在其他樓層時不動()
    {
        var (_, motion) = Build(
            E(1, EffectKinds.PalletRead, (FieldKinds.Conv, "C1"), (FieldKinds.Pallet, "P-001")), // 第一次被讀到：前一個位置不明
            E(2, EffectKinds.PalletRead, (FieldKinds.Conv, "Z"), (FieldKinds.Pallet, "P-002")),
            E(3, EffectKinds.PalletRead, (FieldKinds.Conv, "C1"), (FieldKinds.Pallet, "P-002"))); // 從 2F 來：不對齊

        var p = motion.PlacementAt("st-1", 3, T0.AddSeconds(20))!;
        Assert.Equal((1.0, 1.0, false), (p.X, p.Y, p.IsEstimated));
    }

    [Fact]
    public void 沒有指定導軌的Conv不移動()
    {
        var (_, motion) = Build(Sample);

        Assert.False(motion.Moves("st-2"));
        Assert.Null(motion.PlacementAt("st-2", 4, T0.AddSeconds(40)));
    }

    [Fact]
    public void 設定檔_Conv指定導軌時格式版本13_工站點不能指定導軌()
    {
        var doc = new PlantConfigDocument { Plant = "範例廠", Layout = Layout() };
        Assert.Equal(13, doc.RequiredFormatVersion());
        Assert.Empty(PlantConfigFile.Validate(doc));

        doc.Layout.Elements.Single(e => e.Id == "st-1").Rail = null;
        Assert.Equal(11, doc.RequiredFormatVersion());

        doc.Layout.Elements.Add(new() { Id = "ws-1", Type = LayoutElementType.Workstation, Name = "工站點", Floor = "1F", Rail = "rl-1", X = 0, Y = 6, W = 2, H = 2, Rotation = 0 });
        Assert.Contains(PlantConfigFile.Validate(doc), i => i.Message.Contains("只有搬送機、RM 與 Conv 可以") && i.ElementIds.SequenceEqual(["ws-1"]));
    }
}
