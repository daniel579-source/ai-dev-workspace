using LogTrace.Engine.Events;
using LogTrace.Engine.Parsing;
using LogTrace.Engine.Playback;
using LogTrace.Engine.Plants;
using LogTrace.Tests.Support;
using LogTrace.Workspace.Layout;
using LogTrace.Workspace.Playback;

namespace LogTrace.Workspace.Tests;

// Conv 搭配導軌移動的回放與編輯（ADR-0028）。LOG、編號、時間與座標全部是虛構的。
public sealed class ConvRailTests : IDisposable
{
    static readonly DateTime T0 = new(2030, 1, 9, 10, 0, 0);
    static readonly int[] Seconds = [0, 0, 10, 20, 40];

    static LogEvent Event(long seq) =>
        new(seq, new StableEventId("PLC", "20300109", "PLC/log.txt", (int)seq), T0.AddSeconds(Seconds[seq]), "", "INFO", "PLC", "PLCLOG", $"虛構事件 {seq}",
            new EventSource("PLC/log.txt", (int)seq, (int)seq, 0, 0), EventFlags.None);

    static EventEffect E(long seq, string effect, params (string Kind, string Value)[] p) =>
        new(seq, effect, p.ToDictionary(x => x.Kind, x => (IReadOnlyList<string>)[x.Value]));

    static readonly LayoutSection Layout = new()
    {
        Elements =
        [
            new() { Id = "rl-1", Type = LayoutElementType.Rail, Points = [new(1, 1), new(21, 1)] },
            new() { Id = "st-1", Type = LayoutElementType.Conv, Name = "移動 Conv", Aliases = ["C1"], Rail = "rl-1", X = 0, Y = 0, W = 2, H = 2, Rotation = 0 },
            new() { Id = "st-2", Type = LayoutElementType.Conv, Name = "Conv A", Aliases = ["A"], X = 5, Y = 3, W = 2, H = 2, Rotation = 0 },
            new() { Id = "vh-1", Type = LayoutElementType.Vehicle, Name = "搬送機 1", Aliases = ["V1"], X = 14, Y = 4, W = 2, H = 1, Rotation = 0 },
        ],
    };

    readonly TempFolder _tmp = new();

    public void Dispose() => _tmp.Dispose();

    static PlaybackViewModel Load()
    {
        var effects = new List<EventEffect>
        {
            E(1, EffectKinds.PalletRead, (FieldKinds.Conv, "A"), (FieldKinds.Pallet, "P-001")),
            E(2, EffectKinds.PalletRead, (FieldKinds.Conv, "C1"), (FieldKinds.Pallet, "P-001")),
            E(3, EffectKinds.CommandCreated, (FieldKinds.Command, "C-001"), (FieldKinds.Pallet, "P-001")),
            E(4, EffectKinds.SegmentDispatched, (FieldKinds.Command, "C-001"), (FieldKinds.Vehicle, "V1"), (FieldKinds.Destination, "A")),
        };
        var p = new PlaybackViewModel();
        p.Load(new WorldModel(effects, Layout, "在席"), Layout, Enumerable.Range(1, 4).Select(i => Event(i)).ToList(), hasStateRules: true);
        return p;
    }

    [Fact]
    public void 回放_Conv依貨物推估位置_場景帶有位置與推估_元素狀態寫出依據()
    {
        var p = Load();
        var conv = p.Elements.Single(e => e.Id == "st-1");

        p.SetMoment(1, T0);
        Assert.False(conv.IsEstimated);
        Assert.False(Assert.Single(p.SceneNow.Elements, s => s.Id == "st-1").Estimated);

        p.SetMoment(2, T0.AddSeconds(10));
        var s2 = Assert.Single(p.SceneNow.Elements, s => s.Id == "st-1");
        Assert.True(s2.Estimated);
        Assert.Equal((6.0, 1.0), (s2.X, s2.Y));
        p.SelectedElement = conv;
        Assert.Contains(p.Detail, d => d.Label == "位置" && d.Value.Contains("對齊「Conv A」") && d.Value.Contains("貨物從那裡進入") && d.Cause == 2);

        p.SetMoment(3, T0.AddSeconds(25)); // 往搬送機 1 移動中
        Assert.Equal(10.5, Assert.Single(p.SceneNow.Elements, s => s.Id == "st-1").X!.Value, 6);
        Assert.Contains(p.Detail, d => d.Label == "位置" && d.Value.Contains("移動中") && d.Value.Contains("搬送機 1"));
    }

    [Fact]
    public void 編輯器_Conv可以選導軌_工站點不行()
    {
        var vm = new LayoutEditorViewModel(new PlantConfigLibrary(Path.Combine(_tmp.Path, "Plants")), new FakeUserInteraction());
        vm.AddElementCommand.Execute(LayoutElementType.Rail);
        var rail = vm.SelectedElement!;
        vm.AddElementCommand.Execute(LayoutElementType.Conv);
        var conv = vm.SelectedElement!;

        Assert.True(conv.CanHaveRail);
        Assert.Contains(vm.RailChoices, r => r.Id == rail.Id);
        conv.Rail = rail.Id;
        Assert.Equal(rail.Id, conv.ToModel().Rail);

        vm.AddElementCommand.Execute(LayoutElementType.Workstation);
        Assert.False(vm.SelectedElement!.CanHaveRail);
    }
}
