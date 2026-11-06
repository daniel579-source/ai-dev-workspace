using LogTrace.Engine.Events;
using LogTrace.Engine.Parsing;
using LogTrace.Engine.Playback;
using LogTrace.Engine.Plants;
using LogTrace.Workspace.Playback;

namespace LogTrace.Workspace.Tests;

// 回放時搬送機沿導軌移動（ADR-0005 第 10 點，#101）。LOG、編號與時間全部是虛構的。
public sealed class VehicleMotionPlaybackTests
{
    static readonly DateTime T0 = new(2026, 1, 1, 8, 0, 0);

    static LogEvent Event(long seq) =>
        new(seq, new StableEventId("PLC", "20260101", "PLC/log.txt", (int)seq), T0.AddSeconds(seq * 10), "", "INFO", "PLC", "PLCLOG", $"虛構事件 {seq}",
            new EventSource("PLC/log.txt", (int)seq, (int)seq, 0, 0), EventFlags.None);

    static EventEffect E(long seq, string effect, params (string Kind, string Value)[] p) =>
        new(seq, effect, p.ToDictionary(x => x.Kind, x => (IReadOnlyList<string>)[x.Value]));

    static PlaybackViewModel Load()
    {
        var layout = new LayoutSection
        {
            Elements =
            [
                new() { Id = "rl-1", Type = LayoutElementType.Rail, Points = RailPath.StraightTemplate(0, 5), Name = "導軌 1" },
                new() { Id = "vh-1", Type = LayoutElementType.Vehicle, Name = "搬送機 1", Aliases = ["V1"], X = 8, Y = 30, W = 2, H = 1, Rotation = 0, Rail = "rl-1" },
                new() { Id = "st-a", Type = LayoutElementType.Station, Name = "A", Aliases = ["A"], X = 1, Y = 7, W = 2, H = 2, Rotation = 0 },
                new() { Id = "st-b", Type = LayoutElementType.Station, Name = "B", Aliases = ["B"], X = 17, Y = 7, W = 2, H = 2, Rotation = 0 },
            ],
        };
        var effects = new List<EventEffect>
        {
            E(2, EffectKinds.SegmentDispatched, (FieldKinds.Command, "C-1"), (FieldKinds.Vehicle, "V1"), (FieldKinds.Destination, "B"), (FieldKinds.Source, "A")),
            E(4, EffectKinds.SegmentArrived, (FieldKinds.Command, "C-1"), (FieldKinds.Destination, "B")),
        };
        var p = new PlaybackViewModel();
        p.Load(new WorldModel(effects, layout, "在席"), layout, Enumerable.Range(1, 5).Select(i => Event(i)).ToList(), hasStateRules: true);
        return p;
    }

    [Fact]
    public void 派送前在Layout的位置_移動中為推估加虛線框_抵達後依LOG()
    {
        var p = Load();
        var vh = p.Elements.Single(e => e.Id == "vh-1");

        p.SetMoment(1);
        Assert.Equal((8d, 30d), (vh.ViewX, vh.ViewY));
        Assert.False(vh.IsEstimated);
        p.SelectElementCommand.Execute(vh);
        Assert.Contains("位置未取得資訊", p.Detail.Single(d => d.Label == "位置").Value);

        p.SetMoment(3, T0.AddSeconds(30)); // 派送 20 秒、抵達 40 秒：一半，中心 x = 10
        Assert.Equal(9, vh.ViewX, 6); // 左上角 = 中心 − 寬 / 2
        Assert.Equal(4.5, vh.ViewY, 6);
        Assert.True(vh.IsEstimated);
        var line = p.Detail.Single(d => d.Label == "位置");
        Assert.StartsWith("推估", line.Value);
        Assert.Equal(2L, line.Cause);

        p.SetMoment(3, T0.AddSeconds(35)); // 播放時落在兩筆事件之間
        Assert.Equal(13, vh.ViewX, 6);

        p.SetMoment(4);
        Assert.Equal(17, vh.ViewX, 6);
        Assert.False(vh.IsEstimated);
        Assert.StartsWith("依 LOG：已抵達", p.Detail.Single(d => d.Label == "位置").Value);
        Assert.Equal(4L, p.Detail.Single(d => d.Label == "位置").Cause);
        Assert.Equal(2L, p.Detail.Single(d => d.Label == "派送").Cause);
    }
}
