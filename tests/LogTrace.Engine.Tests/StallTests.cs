using LogTrace.Engine.Events;
using LogTrace.Engine.Parsing;
using LogTrace.Engine.Playback;
using LogTrace.Engine.Plants;

namespace LogTrace.Engine.Tests;

// 可能卡住（推估）（ADR-0005 第 5 點 2026-11-16 補充，#142）。編號與時間全部是虛構的。
public sealed class StallTests
{
    static readonly DateTime T0 = new(2030, 1, 7, 10, 0, 0);

    static readonly LayoutSection Layout = new()
    {
        Elements =
        [
            new() { Id = "st-1", Type = LayoutElementType.Station, Aliases = ["A-01"], X = 1, Y = 1, W = 2, H = 2, Rotation = 0 },
            new() { Id = "st-2", Type = LayoutElementType.Station, Aliases = ["A-02"], X = 5, Y = 1, W = 2, H = 2, Rotation = 0 },
            new() { Id = "vh-1", Type = LayoutElementType.Vehicle, Aliases = ["VH01"], X = 3, Y = 4, W = 1, H = 1, Rotation = 0 },
        ],
    };

    /// <summary>(序號, 距 T0 的秒數, 效果, 參數)。</summary>
    static (WorldModel Model, Func<long, DateTime?> TimeOf, DateTime End) Build(params (long Seq, int Sec, string Effect, (string Kind, string Value)[] P)[] items)
    {
        var effects = items.Select(i => new EventEffect(i.Seq, i.Effect,
            i.P.GroupBy(x => x.Kind).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Value).ToList()))).ToList();
        var times = items.ToDictionary(i => i.Seq, i => T0.AddSeconds(i.Sec));
        return (new WorldModel(effects, Layout, "在席"), s => times.TryGetValue(s, out var t) ? t : null, times.Values.Max());
    }

    static (string, string)[] P(params (string, string)[] p) => p;

    [Fact]
    public void 命令某一段之後超過門檻沒有下一步_標示從門檻之後到下一步()
    {
        var (model, timeOf, end) = Build(
            (1, 0, EffectKinds.CommandCreated, P((FieldKinds.Command, "C-001"), (FieldKinds.Pallet, "P-001"))),
            (2, 10, EffectKinds.SegmentDispatched, P((FieldKinds.Command, "C-001"), (FieldKinds.Segment, "1"), (FieldKinds.Vehicle, "VH01"), (FieldKinds.Destination, "A-02"))),
            (3, 70, EffectKinds.SegmentArrived, P((FieldKinds.Command, "C-001"), (FieldKinds.Segment, "1"), (FieldKinds.Destination, "A-02"))),
            (4, 70 + 420, EffectKinds.CommandCompleted, P((FieldKinds.Command, "C-001"))));

        var stall = Assert.Single(Stalls.Find(model, timeOf, end, 300, []));

        Assert.Equal(StallKind.CommandStep, stall.Kind);
        Assert.Equal(("C-001", "P-001", 3L, (long?)4), (stall.Command, stall.Pallet, stall.LastSequence, stall.NextSequence));
        Assert.Equal(T0.AddSeconds(70 + 300), stall.From);
        Assert.Equal(T0.AddSeconds(70 + 420), stall.To);
        Assert.Equal("命令 C-001 第 1 段抵達後 7 分鐘沒有下一步", stall.Reason);
        Assert.False(stall.OverlapsNoLog);
        Assert.False(stall.IsActiveAt(T0.AddSeconds(70 + 299)));
        Assert.True(stall.IsActiveAt(T0.AddSeconds(70 + 300)));
        Assert.False(stall.IsActiveAt(T0.AddSeconds(70 + 420))); // 下一步之後不再標示
    }

    [Fact]
    public void 沒有超過門檻_或命令已結束_不標示()
    {
        var (model, timeOf, end) = Build(
            (1, 0, EffectKinds.SegmentDispatched, P((FieldKinds.Command, "C-002"), (FieldKinds.Vehicle, "VH01"), (FieldKinds.Destination, "A-02"))),
            (2, 300, EffectKinds.SegmentArrived, P((FieldKinds.Command, "C-002"), (FieldKinds.Destination, "A-02"))), // 剛好 5 分鐘，不超過
            (3, 310, EffectKinds.CommandCompleted, P((FieldKinds.Command, "C-002"))),
            (4, 2000, EffectKinds.Signal, P((FieldKinds.Station, "A-01"), (FieldKinds.Signal, "在席"), (FieldKinds.SignalValue, "ON"))));

        Assert.Empty(Stalls.Find(model, timeOf, end, 300, []));
    }

    [Fact]
    public void 命令到案件結束都沒有下一步_標示到案件結束()
    {
        var (model, timeOf, end) = Build(
            (1, 0, EffectKinds.SegmentDispatched, P((FieldKinds.Command, "C-003"), (FieldKinds.Vehicle, "VH01"), (FieldKinds.Destination, "A-02"))),
            (2, 900, EffectKinds.Signal, P((FieldKinds.Station, "A-01"), (FieldKinds.Signal, "在席"), (FieldKinds.SignalValue, "ON"))));

        var stall = Assert.Single(Stalls.Find(model, timeOf, end, 300, []));

        Assert.Null(stall.NextSequence);
        Assert.Equal(end, stall.To);
        Assert.True(stall.IsActiveAt(end));
        Assert.Contains("到案件結束都沒有下一步", stall.Reason);
    }

    [Fact]
    public void 棧板在站點被讀到後超過門檻沒有派送_重複讀到時從第一次開始算()
    {
        var (model, timeOf, end) = Build(
            (1, 0, EffectKinds.PalletRead, P((FieldKinds.Station, "A-01"), (FieldKinds.Pallet, "P-004"))),
            (2, 60, EffectKinds.PalletRead, P((FieldKinds.Station, "A-01"), (FieldKinds.Pallet, "P-004"))),
            (3, 400, EffectKinds.CommandCreated, P((FieldKinds.Command, "C-004"), (FieldKinds.Pallet, "P-004"))),
            (4, 420, EffectKinds.SegmentDispatched, P((FieldKinds.Command, "C-004"), (FieldKinds.Vehicle, "VH01"), (FieldKinds.Destination, "A-02"))),
            (5, 480, EffectKinds.SegmentArrived, P((FieldKinds.Command, "C-004"), (FieldKinds.Destination, "A-02"))),
            (6, 490, EffectKinds.CommandCompleted, P((FieldKinds.Command, "C-004"))));

        var stall = Assert.Single(Stalls.Find(model, timeOf, end, 300, []));

        Assert.Equal(StallKind.WaitingDispatch, stall.Kind);
        Assert.Equal(("P-004", 1L, (long?)4), (stall.Pallet, stall.LastSequence, stall.NextSequence));
        Assert.Equal("棧板 P-004 在站點 A-01 被讀到後 7 分鐘沒有下一步", stall.Reason);
    }

    [Fact]
    public void 與沒有LOG的時段重疊時_標示重疊_不判定卡住()
    {
        var (model, timeOf, end) = Build(
            (1, 0, EffectKinds.SegmentDispatched, P((FieldKinds.Command, "C-005"), (FieldKinds.Vehicle, "VH01"), (FieldKinds.Destination, "A-02"))),
            (2, 600, EffectKinds.SegmentArrived, P((FieldKinds.Command, "C-005"), (FieldKinds.Destination, "A-02"))),
            (3, 610, EffectKinds.CommandCompleted, P((FieldKinds.Command, "C-005"))));
        var gap = new LogGap("PLC", T0.AddSeconds(100), T0.AddSeconds(500));

        Assert.True(Assert.Single(Stalls.Find(model, timeOf, end, 300, [gap])).OverlapsNoLog);
    }

    [Fact]
    public void 門檻可以調整_0表示不判定()
    {
        var (model, timeOf, end) = Build(
            (1, 0, EffectKinds.SegmentDispatched, P((FieldKinds.Command, "C-006"), (FieldKinds.Vehicle, "VH01"), (FieldKinds.Destination, "A-02"))),
            (2, 200, EffectKinds.SegmentArrived, P((FieldKinds.Command, "C-006"), (FieldKinds.Destination, "A-02"))),
            (3, 210, EffectKinds.CommandCompleted, P((FieldKinds.Command, "C-006"))));

        Assert.Empty(Stalls.Find(model, timeOf, end, 300, []));
        Assert.Single(Stalls.Find(model, timeOf, end, 120, []));
        Assert.Empty(Stalls.Find(model, timeOf, end, 0, []));
    }

    [Fact]
    public void 廠別設定的門檻_讀寫往返_舊版沒有欄位時為預設300秒_負數無法存檔()
    {
        var old = PlantConfigFile.Deserialize("""{ "formatVersion": 7, "plant": "舊廠" }""");
        Assert.Null(old.StallThresholdSeconds);
        Assert.Equal(300, old.EffectiveStallThresholdSeconds);
        Assert.Empty(PlantConfigFile.Validate(old));

        var doc = new PlantConfigDocument { Plant = "範例廠", StallThresholdSeconds = 600 };
        var json = PlantConfigFile.Serialize(doc);
        Assert.Contains("\"stallThresholdSeconds\": 600", json);
        Assert.Equal(600, PlantConfigFile.Deserialize(json).EffectiveStallThresholdSeconds);
        Assert.Equal(1, doc.RequiredFormatVersion()); // 舊版程式略過不認得的欄位，不升格式版本

        doc.StallThresholdSeconds = -1;
        Assert.Contains(PlantConfigFile.Validate(doc), i => i.Message.Contains("可能卡住的門檻必須是 0 以上的整數秒"));
    }
}
