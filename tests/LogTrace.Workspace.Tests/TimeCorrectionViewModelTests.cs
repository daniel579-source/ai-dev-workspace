using LogTrace.Engine.Events;
using LogTrace.Tests.Support;

namespace LogTrace.Workspace.Tests;

// 時區與時間偏移量的畫面（#19）。測試資料：tests/fixtures/two-modules（虛構）：EXE 09:00:00、09:00:05；PLC 09:00:00、09:00:01
public sealed class TimeCorrectionViewModelTests : IDisposable
{
    readonly TempFolder _tmp = new();
    readonly FakeUserInteraction _ui = new();
    readonly MainViewModel _vm;

    public TimeCorrectionViewModelTests() =>
        _vm = new MainViewModel(new SettingsStore(Path.Combine(_tmp.Path, "settings.json")), _ui, Path.Combine(_tmp.Path, "Cases"), _ => false);

    public void Dispose()
    {
        _vm.Dispose();
        _tmp.Dispose();
    }

    async Task Open()
    {
        _vm.NewCaseName = "偏移量";
        _vm.CreateCaseCommand.Execute(null);
        _vm.ImportSource = TempFolder.Fixture("two-modules");
        await _vm.ImportCommand.ExecuteAsync(null);
    }

    string Order() => string.Join(" ", _vm.Events.Select(e => $"{e.Module}@{e.Time[11..]}"));

    /// <summary>在對話框中填入偏移量並按確定。</summary>
    void Answer(string caseOffset, params (string Module, string Offset)[] modules) => _ui.TimeCorrection = editor =>
    {
        editor.CaseOffset = caseOffset;
        foreach (var (module, offset) in modules) editor.Modules.Single(m => m.Module == module).Offset = offset;
        return true;
    };

    [Fact]
    public async Task 畫面頂端顯示時區_沒有偏移量時不顯示已校正()
    {
        Assert.Equal("時區：尚未開啟案件", _vm.TimeZoneText);
        Assert.False(_vm.EditTimeCorrectionCommand.CanExecute(null));

        await Open();

        Assert.Equal("時區：Asia/Taipei（UTC+08:00）", _vm.TimeZoneText);
        Assert.Null(_vm.CorrectionText);
        Assert.True(_vm.EditTimeCorrectionCommand.CanExecute(null));
    }

    [Fact]
    public async Task 設定模組偏移量_顯示時間改變並重新排序_標示已校正_原始時間仍可查看()
    {
        await Open();
        Assert.Equal("EXE@09:00:00.0000 PLC@09:00:00.0000 PLC@09:00:01.0000 EXE@09:00:05.0000", Order());
        _vm.SelectedEvent = _vm.Events.Single(e => e.Module == "PLC" && e.Time.EndsWith("01.0000"));
        Answer("", ("PLC", "4.5"));

        await _vm.EditTimeCorrectionCommand.ExecuteAsync(null);

        Assert.Equal("EXE@09:00:00.0000 PLC@09:00:04.5000 EXE@09:00:05.0000 PLC@09:00:05.5000", Order());
        Assert.Equal("已校正（PLC +4.5 秒）", _vm.CorrectionText);
        Assert.All(_vm.Events, e => Assert.Equal(e.Module == "PLC", e.Flags.Any(f => f.Flag == EventFlags.Corrected)));
        // 回到原本選取的事件；詳細資訊有原始時間與校正前後的時間
        Assert.Equal("PLC@09:00:05.5000", $"{_vm.SelectedEvent!.Module}@{_vm.SelectedEvent.Time[11..]}");
        Assert.Contains("原始時間：2030-01-07 09:00:01.0000", _vm.SelectedDetail);
        Assert.Contains("已校正：+4.5 秒（校正前 2030-01-07 09:00:01.0000，校正後 2030-01-07 09:00:05.5000）", _vm.SelectedDetail);
    }

    [Fact]
    public async Task 對話框顯示目前的偏移量_改回0後恢復()
    {
        await Open();
        Answer("+2", ("EXE", "-1.25"));
        await _vm.EditTimeCorrectionCommand.ExecuteAsync(null);
        Assert.Equal("已校正（整體 +2 秒；EXE −1.25 秒）", _vm.CorrectionText);

        TimeCorrectionViewModel? shown = null;
        _ui.TimeCorrection = editor =>
        {
            shown = editor;
            Assert.Equal("+2", editor.CaseOffset);
            Assert.Equal(["EXE", "PLC"], editor.Modules.Select(m => m.Module));
            Assert.Equal(["-1.25", ""], editor.Modules.Select(m => m.Offset));
            editor.ResetCommand.Execute(null);
            return true;
        };
        await _vm.EditTimeCorrectionCommand.ExecuteAsync(null);

        Assert.NotNull(shown);
        Assert.Null(_vm.CorrectionText);
        Assert.Equal("EXE@09:00:00.0000 PLC@09:00:00.0000 PLC@09:00:01.0000 EXE@09:00:05.0000", Order());
        Assert.DoesNotContain(_vm.Events, e => e.Flags.Any(f => f.Flag == EventFlags.Corrected));
    }

    [Fact]
    public async Task 輸入有誤時不套用_說明哪一個欄位()
    {
        await Open();
        var editor = new TimeCorrectionViewModel(TimeCorrection.None, ["EXE", "PLC"]) { CaseOffset = "abc" };
        editor.Modules[1].Offset = "0.00001";

        Assert.False(editor.TryAccept());
        Assert.Contains("整體偏移量、PLC", editor.Error);
        Assert.Null(editor.Result);

        Answer("abc");
        await _vm.EditTimeCorrectionCommand.ExecuteAsync(null);
        Assert.Null(_vm.CorrectionText);
        Assert.Equal("EXE@09:00:00.0000 PLC@09:00:00.0000 PLC@09:00:01.0000 EXE@09:00:05.0000", Order());
    }

    [Fact]
    public async Task 已校正的旗標有圖例()
    {
        await Open();
        Assert.Contains(_vm.FlagLegend, f => f.Flag == EventFlags.Corrected && f.Name == "已校正");
    }
}
