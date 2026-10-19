using LogTrace.Tests.Support;

namespace LogTrace.Workspace.Tests;

// 目前時刻、方向鍵、播放、時間軸、只看這個元素（#25）。測試資料：tests/fixtures/playback-prototype（虛構，案件從 10:00:00 起）
public sealed class MomentViewModelTests : IDisposable
{
    static readonly DateTime T0 = new(2030, 1, 11, 10, 0, 0);

    readonly TempFolder _tmp = new();
    readonly MainViewModel _vm;

    public MomentViewModelTests() =>
        _vm = new MainViewModel(new SettingsStore(Path.Combine(_tmp.Path, "settings.json")), new FakeUserInteraction(), Path.Combine(_tmp.Path, "Cases"), _ => false);

    public void Dispose()
    {
        _vm.Dispose();
        _tmp.Dispose();
    }

    async Task Open(params (string Code, string Name)[] modeNames)
    {
        var plants = Directory.CreateDirectory(Path.Combine(_tmp.Path, "Plants")).FullName;
        var doc = Engine.Plants.PlantConfigFile.Load(Path.Combine(TempFolder.Fixture("playback-prototype"), "plant.json"));
        doc.StationModeNames = modeNames.Select(n => KeyValuePair.Create(n.Code, n.Name)).ToList();
        Engine.Plants.PlantConfigFile.Save(doc, Path.Combine(plants, "範例廠.json"));
        _vm.RefreshPlants();
        _vm.SelectedPlant = _vm.Plants.Single(p => p.Path is not null);
        _vm.NewCaseName = "回放";
        _vm.CreateCaseCommand.Execute(null);
        _vm.ImportSource = Path.Combine(TempFolder.Fixture("playback-prototype"), "logs");
        await _vm.ImportCommand.ExecuteAsync(null);
    }

    static DateTime At(double seconds) => T0.AddSeconds(seconds);

    string Selected => _vm.SelectedEvent!.Event.Message;

    [Fact]
    public async Task 改變目前時刻後_事件清單_Layout_詳細資訊_原始LOG_時間軸都同步()
    {
        await Open();

        _vm.SeekTime(At(100.5)); // 10:01:40.5：SRCA01 連線 ON-LINE 之後

        Assert.Equal(At(100.5), _vm.CurrentTime);
        Assert.Contains("SRCA01 連線狀態 ON-LINE", Selected);                         // 事件清單：該時刻之前最後一筆
        Assert.Contains($"事件 #{_vm.SelectedEvent!.Sequence}", _vm.Playback.MomentText); // Layout
        Assert.Contains("SRCA01 連線狀態 ON-LINE", _vm.SelectedDetail);                // 詳細資訊
        Assert.EndsWith("WCSDebug.txt", _vm.RawFile);                                   // 原始 LOG
        Assert.Equal(At(100.5), _vm.Timeline.Current);                                  // 時間軸
        Assert.Equal(_vm.Timeline.FractionOf(At(100.5)), _vm.Timeline.Cursor);
        _vm.Playback.SelectElementCommand.Execute(_vm.Playback.Elements.Single(e => e.Id == "vh-1"));
        Assert.Equal("ON-LINE", _vm.Playback.Detail.Single(d => d.Label == "連線").Value);

        _vm.SelectedEvent = _vm.Events.First(); // 在事件清單點選：目前時刻跟著移動
        Assert.Equal(_vm.Events.First().Event.Timestamp, _vm.CurrentTime);
        Assert.Equal("未知", _vm.Playback.Detail.Single(d => d.Label == "連線").Value);
    }

    [Fact]
    public async Task 站點模式_廠別設定有名稱時顯示名稱_沒有時顯示代號()
    {
        await Open(("1", "虛構模式甲"));
        _vm.SeekTime(At(2.5)); // A-01 站點模式代號 -> 1 之後
        _vm.Playback.SelectElementCommand.Execute(_vm.Playback.Elements.Single(e => e.Id == "st-1"));

        var mode = _vm.Playback.Detail.Single(d => d.Label == "站點模式");
        Assert.Equal("虛構模式甲", mode.Value);
        Assert.NotNull(mode.Cause); // 仍可追溯到原始事件
        Assert.Equal("7", _vm.Playback.StationModeText("7")); // 沒填的代號
        Assert.Equal("未知", _vm.Playback.StationModeText(null));
        Assert.StartsWith("在席 未知；模式 虛構模式甲", _vm.Playback.Equipment.Single(e => e.Id == "st-1").State); // 設備狀態表

        _vm.SeekTime(At(1)); // 模式還未知：設備狀態表不顯示模式
        Assert.DoesNotContain("模式", _vm.Playback.Equipment.Single(e => e.Id == "st-1").State);
    }

    [Fact]
    public async Task 方向鍵_狀態變化_到頭尾時停在原地並說明()
    {
        await Open();
        _vm.GoToStartCommand.Execute(null); // 第一筆是 A-09 心跳（不是狀態變化）

        _vm.NextChangeCommand.Execute(null);
        Assert.Contains("站點模式代號 -> 1", Selected);
        _vm.NextChangeCommand.Execute(null);
        Assert.Contains("在席 = ON", Selected);
        _vm.PreviousChangeCommand.Execute(null);
        Assert.Contains("站點模式代號 -> 1", Selected);
        _vm.PreviousChangeCommand.Execute(null);
        Assert.Contains("站點模式代號 -> 1", Selected);
        Assert.Equal("已經是第一個狀態變化。", _vm.Status);

        _vm.GoToEndCommand.Execute(null);
        _vm.NextChangeCommand.Execute(null);
        Assert.Equal("已經是最後一個狀態變化。", _vm.Status);
        _vm.PreviousChangeCommand.Execute(null);
        Assert.Equal(At(306), _vm.CurrentTime); // 最後一個狀態變化：A-01 在席 OFF
        Assert.Contains("[A-01][Station_Status_Change] => 在席 = OFF", Selected);
    }

    [Fact]
    public async Task 方向鍵_逐筆事件_Home_End()
    {
        await Open();

        _vm.GoToEndCommand.Execute(null);
        Assert.Same(_vm.Events[^1], _vm.SelectedEvent);
        _vm.NextEventCommand.Execute(null);
        Assert.Same(_vm.Events[^1], _vm.SelectedEvent);
        Assert.Equal("已經是最後一筆事件。", _vm.Status);
        _vm.PreviousEventCommand.Execute(null);
        Assert.Same(_vm.Events[^2], _vm.SelectedEvent);

        _vm.GoToStartCommand.Execute(null);
        Assert.Same(_vm.Events[0], _vm.SelectedEvent);
        _vm.PreviousEventCommand.Execute(null);
        Assert.Equal("已經是第一筆事件。", _vm.Status);
        _vm.NextEventCommand.Execute(null);
        Assert.Same(_vm.Events[1], _vm.SelectedEvent);
    }

    [Fact]
    public async Task 方向鍵_符合篩選條件的事件()
    {
        await Open();
        _vm.SearchAllOf = "SSCA01";
        _vm.ShowAllWithMarks = true;
        await _vm.SearchCommand.ExecuteAsync(null);
        _vm.GoToStartCommand.Execute(null);

        _vm.NextMatchCommand.Execute(null);
        Assert.Contains("派送 00040001 第1段 → SSCA01", Selected);
        _vm.NextMatchCommand.Execute(null);
        Assert.Contains("00040001 第1段 抵達 1003（SSCA01）", Selected);
    }

    [Fact]
    public async Task 播放_依速度前進_到結尾時停止()
    {
        await Open();
        _vm.SkipIdle = false;
        _vm.SeekTime(At(2));

        _vm.TogglePlayCommand.Execute(null);
        _vm.Tick(TimeSpan.FromSeconds(1));
        Assert.Equal(At(3), _vm.CurrentTime);
        _vm.Speed = 60;
        _vm.Tick(TimeSpan.FromSeconds(1));
        Assert.Equal(At(63), _vm.CurrentTime);

        _vm.Tick(TimeSpan.FromHours(1));
        Assert.False(_vm.IsPlaying);
        Assert.Equal(_vm.Timeline.End, _vm.CurrentTime);
        Assert.Same(_vm.Events[^1], _vm.SelectedEvent);

        _vm.TogglePlayCommand.Execute(null); // 在結尾按播放：從頭開始
        Assert.True(_vm.IsPlaying);
        Assert.Equal(_vm.Timeline.Start, _vm.CurrentTime);
        _vm.TogglePlayCommand.Execute(null);
        _vm.Tick(TimeSpan.FromSeconds(1)); // 暫停時不前進
        Assert.Equal(_vm.Timeline.Start, _vm.CurrentTime);
    }

    [Fact]
    public async Task 播放_略過閒置時段_超過10秒沒有狀態變化時直接跳到下一個變化()
    {
        await Open();
        _vm.SeekTime(At(46)); // A-03 在席 OFF；下一個狀態變化是 95 秒的抵達儲位
        _vm.TogglePlayCommand.Execute(null);

        _vm.Tick(TimeSpan.FromMilliseconds(100));

        Assert.Equal(At(95), _vm.CurrentTime);
        Assert.Contains("00040001 第2段 抵達 0100302", Selected);

        _vm.Tick(TimeSpan.FromMilliseconds(100)); // 下一個變化（96 秒）在 10 秒內：照速度前進
        Assert.Equal(T0.AddMilliseconds(95_100), _vm.CurrentTime);

        _vm.SkipIdle = false;
        _vm.SeekTime(At(46));
        _vm.Tick(TimeSpan.FromMilliseconds(100));
        Assert.Equal(T0.AddMilliseconds(46_100), _vm.CurrentTime);
    }

    [Fact]
    public async Task 時間軸_總覽的密度_警報期間_搜尋符合的位置與搜尋結果一致()
    {
        await Open();

        Assert.Equal(T0.AddSeconds(1.3), _vm.Timeline.Start);
        Assert.Equal(At(360), _vm.Timeline.End);
        Assert.Equal(1.0, _vm.Timeline.Density.Max());
        Assert.Equal(2, _vm.Timeline.AlarmSpans.Count);
        var vehicle = _vm.Timeline.AlarmSpans.Single(s => s.Label.Contains("E101"));
        Assert.Equal((_vm.Timeline.FractionOf(At(176)), _vm.Timeline.FractionOf(At(230)), false), (vehicle.From, vehicle.To, vehicle.NeverReset));
        var station = _vm.Timeline.AlarmSpans.Single(s => s.Label.Contains("E205"));
        Assert.Equal((1.0, true), (station.To, station.NeverReset)); // 沒有復歸：延伸到案件結束
        Assert.Contains("輸送站2", station.Label);

        _vm.SearchAllOf = "SSCA01";
        await _vm.SearchCommand.ExecuteAsync(null);
        var expected = _vm.Events.Select(e => _vm.Timeline.FractionOf(e.Event.Timestamp)).Distinct();
        Assert.Equal(expected, _vm.Timeline.SearchMarks);

        _vm.ClearSearchCommand.Execute(null);
        Assert.Empty(_vm.Timeline.SearchMarks);
    }

    [Fact]
    public async Task 時間軸_細部是目前時刻前後30秒_狀態變化為長刻度_點擊跳到該時刻()
    {
        await Open();
        _vm.SeekTime(At(100));

        var ticks = _vm.Timeline.DetailTicks;
        Assert.All(ticks, t => Assert.InRange(t.Fraction, 0, 1));
        Assert.Contains(ticks, t => t.IsChange && Math.Abs(t.Fraction - 0.5) < 1e-9); // 100 秒的連線狀態
        Assert.Contains(ticks, t => !t.IsChange);                                       // A-09 心跳

        _vm.SeekTime(_vm.Timeline.DetailTime(1.0));
        Assert.Equal(At(130), _vm.CurrentTime);
        _vm.SeekTime(_vm.Timeline.OverviewTime(0));
        Assert.Equal(_vm.Timeline.Start, _vm.CurrentTime);
    }

    [Fact]
    public async Task 只看這個元素_事件清單與移動都只針對選取的元素()
    {
        await Open();
        _vm.Playback.SelectElementCommand.Execute(_vm.Playback.Elements.Single(e => e.Id == "st-4"));

        await _vm.FocusSelectedElementCommand.ExecuteAsync(null);

        Assert.Equal("出庫站4", _vm.FocusedElement);
        Assert.Contains("只看「出庫站4」", _vm.SearchStatus);
        Assert.All(_vm.Events, e => Assert.True(e.Event.Message.Contains("A-04") || e.Event.Message.Contains("1004")));
        _vm.GoToStartCommand.Execute(null);
        Assert.Contains("派送 00040002 第1段 → SRCA01 目的地 1004", Selected); // 以出庫站4 為目的地
        _vm.NextChangeCommand.Execute(null);
        Assert.Contains("[A-04][Station_Status_Change] => 在席 = OFF", Selected);
        _vm.NextChangeCommand.Execute(null);
        Assert.Contains("00040002 第1段 抵達 1004", Selected);
        _vm.NextEventCommand.Execute(null);
        Assert.Equal("已經是最後一筆事件。", _vm.Status);

        _vm.ClearSearchCommand.Execute(null);
        Assert.Null(_vm.FocusedElement);
        Assert.Equal(44, _vm.Events.Count);
    }
}
