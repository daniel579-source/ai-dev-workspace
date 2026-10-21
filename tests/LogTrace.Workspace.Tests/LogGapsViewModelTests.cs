using LogTrace.Engine.Plants;
using LogTrace.Tests.Support;

namespace LogTrace.Workspace.Tests;

// 沒有 LOG 的時段（#84，FR-037、AC-011）。測試資料：tests/fixtures/log-gaps（虛構）——
// PLC 每 30 秒一筆心跳，刻意刪除 08:10～08:20 之間的 LOG；EXE 最後一筆在 08:24，案件到 08:30。
public sealed class LogGapsViewModelTests : IDisposable
{
    static readonly DateTime T0 = new(2030, 1, 12, 8, 0, 0);

    readonly TempFolder _tmp = new();
    readonly MainViewModel _vm;

    public LogGapsViewModelTests() =>
        _vm = new MainViewModel(new SettingsStore(Path.Combine(_tmp.Path, "settings.json")), new FakeUserInteraction(), Path.Combine(_tmp.Path, "Cases"), _ => false);

    public void Dispose()
    {
        _vm.Dispose();
        _tmp.Dispose();
    }

    async Task Open(params (string Module, int Seconds)[] thresholds)
    {
        var plants = Directory.CreateDirectory(Path.Combine(_tmp.Path, "Plants")).FullName;
        var doc = new PlantConfigDocument { Plant = "範例廠", GapThresholds = thresholds.Select(t => KeyValuePair.Create(t.Module, t.Seconds)).ToList() };
        PlantConfigFile.Save(doc, Path.Combine(plants, "範例廠.json"));
        _vm.RefreshPlants();
        _vm.SelectedPlant = _vm.Plants.Single(p => p.Path is not null);
        _vm.NewCaseName = "缺口";
        _vm.CreateCaseCommand.Execute(null);
        _vm.ImportSource = TempFolder.Fixture("log-gaps");
        await _vm.ImportCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task 時間軸標出刻意刪除的時段與模組結尾_每個模組一列()
    {
        await Open();

        Assert.Equal(["EXE", "PLC"], _vm.Timeline.GapRows.OrderBy(m => m, StringComparer.Ordinal)); // 每個模組一列
        var gaps = _vm.Timeline.Gaps.Select(g => (g.Gap.Module, g.Gap.From, g.Gap.To)).ToList();
        Assert.Equal([("PLC", T0.AddMinutes(10), T0.AddMinutes(20)), ("EXE", T0.AddMinutes(24), T0.AddMinutes(30))], gaps);
        var plc = _vm.Timeline.Gaps[0];
        Assert.Equal(_vm.Timeline.FractionOf(T0.AddMinutes(10)), plc.From);
        Assert.Equal("模組 PLC：08:10:00～08:20:00 沒有 LOG（10 分鐘；可能是閒置或缺漏）",
            TimelineDescribe(_vm.Timeline.GapsAtPosition(_vm.Timeline.FractionOf(T0.AddMinutes(15))).Single()));
    }

    static string TimelineDescribe(Engine.Events.LogGap gap) => Playback.TimelineViewModel.Describe(gap);

    [Fact]
    public async Task 目前時刻在時段中時_回放與詳細資訊提示_離開後消失()
    {
        await Open();

        _vm.SeekTime(T0.AddMinutes(15));

        const string notice = "目前時刻 模組 PLC 沒有 LOG（08:10:00～08:20:00，10 分鐘；可能是閒置或缺漏）";
        Assert.Equal(notice, _vm.GapNotice);
        Assert.Equal(notice, _vm.Playback.GapNotice);
        Assert.Contains(notice, _vm.SelectedDetail);

        _vm.SeekTime(T0.AddMinutes(5));
        Assert.Null(_vm.GapNotice);
        Assert.Null(_vm.Playback.GapNotice);
        Assert.DoesNotContain("沒有 LOG", _vm.SelectedDetail);
    }

    [Fact]
    public async Task 兩個模組同時沒有LOG時_都列出()
    {
        await Open(("EXE", 60));

        _vm.SeekTime(T0.AddMinutes(11));

        Assert.Equal(2, _vm.GapNotice!.Split('\n').Length);
        Assert.Contains("模組 PLC", _vm.GapNotice);
        Assert.Contains("模組 EXE 沒有 LOG（08:08:00～08:12:00，4 分鐘", _vm.GapNotice);
    }

    [Fact]
    public async Task 依廠別設定的門檻_0表示不標示()
    {
        await Open(("exe", 0), ("PLC", 900));

        Assert.Empty(_vm.Timeline.Gaps); // PLC 的 10 分鐘未超過 15 分鐘；EXE 不標示
    }

    [Fact]
    public async Task 關閉案件後清空()
    {
        await Open();
        _vm.SeekTime(T0.AddMinutes(15));

        _vm.CloseCaseCommand.Execute(null);

        Assert.Null(_vm.GapNotice);
        Assert.Empty(_vm.Timeline.Gaps);
    }
}
