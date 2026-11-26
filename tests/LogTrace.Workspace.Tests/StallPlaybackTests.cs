using LogTrace.Engine.Events;
using LogTrace.Engine.Parsing;
using LogTrace.Engine.Playback;
using LogTrace.Engine.Plants;
using LogTrace.Workspace.Playback;

namespace LogTrace.Workspace.Tests;

// 可能卡住（推估）的顯示（ADR-0005 第 5 點 2026-11-16 補充，#142）。LOG、編號與時間全部是虛構的。
public sealed class StallPlaybackTests
{
    static readonly DateTime T0 = new(2030, 1, 7, 10, 0, 0);

    // 事件序號 → 時間（秒）：1 讀到、2 建立、3 派送、4 抵達站點 B、5 案件中間的其他事件、6 命令結束（抵達後 8 分鐘）
    static readonly int[] Seconds = [0, 0, 20, 30, 90, 400, 570];

    static LogEvent Event(long seq) =>
        new(seq, new StableEventId("PLC", "20300107", "PLC/log.txt", (int)seq), T0.AddSeconds(Seconds[seq]), "", "INFO", "PLC", "PLCLOG", $"虛構事件 {seq}",
            new EventSource("PLC/log.txt", (int)seq, (int)seq, 0, 0), EventFlags.None);

    static EventEffect E(long seq, string effect, params (string Kind, string Value)[] p) =>
        new(seq, effect, p.ToDictionary(x => x.Kind, x => (IReadOnlyList<string>)[x.Value]));

    static readonly LayoutSection Layout = new()
    {
        Elements =
        [
            new() { Id = "st-a", Type = LayoutElementType.Conv, Name = "A", Aliases = ["A"], X = 1, Y = 1, W = 2, H = 2, Rotation = 0 },
            new() { Id = "st-b", Type = LayoutElementType.Conv, Name = "B", Aliases = ["B"], X = 9, Y = 1, W = 2, H = 2, Rotation = 0 },
            new() { Id = "vh-1", Type = LayoutElementType.Vehicle, Name = "搬送機 1", Aliases = ["V1"], X = 5, Y = 4, W = 1, H = 1, Rotation = 0 },
        ],
    };

    static (PlaybackViewModel Playback, IReadOnlyList<Stall> Stalls, List<LogEvent> Events) Load(IReadOnlyList<LogGap>? gaps = null)
    {
        var effects = new List<EventEffect>
        {
            E(1, EffectKinds.PalletRead, (FieldKinds.Conv, "A"), (FieldKinds.Pallet, "P-001")),
            E(2, EffectKinds.CommandCreated, (FieldKinds.Command, "C-001"), (FieldKinds.Pallet, "P-001")),
            E(3, EffectKinds.SegmentDispatched, (FieldKinds.Command, "C-001"), (FieldKinds.Vehicle, "V1"), (FieldKinds.Destination, "B")),
            E(4, EffectKinds.SegmentArrived, (FieldKinds.Command, "C-001"), (FieldKinds.Destination, "B")),
            E(6, EffectKinds.CommandCompleted, (FieldKinds.Command, "C-001")),
        };
        var events = Enumerable.Range(1, 6).Select(i => Event(i)).ToList();
        var model = new WorldModel(effects, Layout, "在席");
        var stalls = Stalls.Find(model, s => events.FirstOrDefault(e => e.Sequence == s)?.Timestamp, events[^1].Timestamp, 300, gaps ?? []);
        var p = new PlaybackViewModel();
        p.Load(model, Layout, events, hasStateRules: true, stalls: stalls);
        return (p, stalls, events);
    }

    [Fact]
    public void 超過門檻後_棧板所在的站點標示可能卡住_詳細資訊寫出依據_下一步之後消失()
    {
        var (p, stalls, _) = Load();
        var b = p.Elements.Single(e => e.Id == "st-b");
        Assert.Equal("命令 C-001 第 1 段抵達後 8 分鐘沒有下一步", Assert.Single(stalls).Reason);

        p.SetMoment(4, T0.AddSeconds(90 + 299));
        Assert.False(b.IsStalled); // 還沒超過門檻

        p.SetMoment(5); // 抵達後 310 秒
        Assert.True(b.IsStalled);
        Assert.True(p.SceneNow.Elements.Single(e => e.Id == "st-b").Stalled);
        Assert.False(p.SceneNow.Elements.Single(e => e.Id == "vh-1").Stalled);
        p.SelectElementCommand.Execute(b);
        var line = p.Detail.Single(d => d.Label == "可能卡住");
        Assert.Contains("推估：命令 C-001 第 1 段抵達後 8 分鐘沒有下一步", line.Value);
        Assert.Equal(4, line.Cause); // 點選跳到最後一次進展的事件
        Assert.Contains("可能卡住（推估）", p.Equipment.Single(r => r.Name == "B").State);

        p.SetMoment(6);
        Assert.False(b.IsStalled);
    }

    [Fact]
    public void 與沒有LOG的時段重疊時_不加標記_只在詳細資訊說明()
    {
        var (p, _, _) = Load([new LogGap("EXE", T0.AddSeconds(100), T0.AddSeconds(500))]);
        var b = p.Elements.Single(e => e.Id == "st-b");

        p.SetMoment(5);
        Assert.False(b.IsStalled);
        p.SelectElementCommand.Execute(b);
        Assert.Contains("不判定為可能卡住", p.Detail.Single(d => d.Label == "超過門檻").Value);
        Assert.DoesNotContain(p.Detail, d => d.Label == "可能卡住");
    }

    [Fact]
    public void 時間軸總覽_和警報同一列以不同顏色標示_重疊的只在滑鼠提示說明()
    {
        var (_, stalls, events) = Load();
        var overlapping = stalls[0] with { OverlapsNoLog = true };
        var rows = events.Select(e => new EventRowViewModel(e)).ToList();
        var t = new TimelineViewModel();

        t.Load(rows, [], [], ["PLC"], [], stalls);
        var span = Assert.Single(t.AlarmSpans);
        Assert.True(span.Stalled);
        Assert.Equal((90 + 300) / 570d, span.From, 6);
        Assert.Equal(1, span.To, 6);
        Assert.StartsWith("可能卡住（推估）", TimelineViewModel.Describe(Assert.Single(t.StallsAtPosition(0.9))));

        t.Load(rows, [], [], ["PLC"], [], [overlapping]);
        Assert.Empty(t.AlarmSpans);
        Assert.Contains("不判定為可能卡住", TimelineViewModel.Describe(Assert.Single(t.StallsAtPosition(0.9))));
    }

    [Fact]
    public async Task 主畫面_匯入卡住的虛構資料後跳到卡住的時刻_站點標示可能卡住()
    {
        using var tmp = new LogTrace.Tests.Support.TempFolder();
        using var vm = new MainViewModel(new SettingsStore(Path.Combine(tmp.Path, "settings.json")), new FakeUserInteraction(), Path.Combine(tmp.Path, "Cases"), _ => false);
        var plants = Directory.CreateDirectory(Path.Combine(tmp.Path, "Plants")).FullName;
        File.Copy(Path.Combine(LogTrace.Tests.Support.TempFolder.Fixture("playback-prototype"), "plant.json"), Path.Combine(plants, "範例廠.json"));
        vm.RefreshPlants();
        vm.SelectedPlant = vm.Plants.Single(p => p.Path is not null);
        vm.NewCaseName = "卡住";
        vm.CreateCaseCommand.Execute(null);
        vm.ImportSource = LogTrace.Tests.Support.TempFolder.Fixture("stall");
        await vm.ImportCommand.ExecuteAsync(null);

        vm.SeekTime(new DateTime(2030, 1, 12, 9, 6, 30));

        var st3 = vm.Playback.Elements.Single(e => e.Id == "st-3");
        Assert.True(st3.IsStalled);
        Assert.True(vm.Playback.SceneNow.Elements.Single(e => e.Id == "st-3").Stalled);
        Assert.Contains(vm.Timeline.AlarmSpans, s => s.Stalled);
    }
}
