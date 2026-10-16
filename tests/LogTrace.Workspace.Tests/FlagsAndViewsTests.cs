using LogTrace.Engine.Cases;
using LogTrace.Engine.Events;
using LogTrace.Tests.Support;

namespace LogTrace.Workspace.Tests;

// 旗標顯示、原始 LOG 同步、檢視切換（測試資料：tests/fixtures/parsing-cases，虛構）
public sealed class FlagsAndViewsTests : IDisposable
{
    const string PlcArchive = "20300108/PLC-2030-01-08/archives/PLCLOG_2030-01-08 08_00_02.0000.txt";
    const string PlcCurrent = "20300108/PLC-2030-01-08/PLCLOG.txt";

    readonly TempFolder _tmp = new();
    readonly MainViewModel _vm;

    public FlagsAndViewsTests() => _vm = new MainViewModel(new CaseLibrary(Path.Combine(_tmp.Path, "Cases")), new NoPicker());

    public void Dispose()
    {
        _vm.Dispose();
        _tmp.Dispose();
    }

    sealed class NoPicker : IFolderPicker
    {
        public string? PickFolder(string title) => null;
    }

    async Task ImportParsingCases()
    {
        _vm.NewCaseName = "解析狀況";
        _vm.CreateCaseCommand.Execute(null);
        _vm.ImportFolder = TempFolder.Fixture("parsing-cases");
        await _vm.ImportCommand.ExecuteAsync(null);
    }

    EventRowViewModel Row(string path, int line) =>
        _vm.Events.Single(r => r.Event.Source.RelativePath == path && r.Event.Source.LineStart == line);

    [Fact]
    public async Task 匯入後_狀態列提到無法解析的檔案()
    {
        await ImportParsingCases();

        Assert.Contains("已匯入 5 個檔案、9 筆事件", _vm.Status);
        Assert.Contains("無法解析的檔案 1 個", _vm.Status);
    }

    [Fact]
    public async Task 旗標以圖示顯示_滑鼠說明與圖例一致()
    {
        await ImportParsingCases();

        var preamble = Row(PlcArchive, 1);
        Assert.Equal("⌀？", preamble.FlagGlyphs); // 時間無效、未知種類（依圖例順序）
        Assert.Contains("時間無效（時間為沿用）", preamble.FlagToolTip);
        Assert.Contains("未知種類", preamble.FlagToolTip);

        Assert.Equal("↶", Row(PlcCurrent, 1).FlagGlyphs);
        Assert.Equal("＝", Row(PlcArchive, 3).FlagGlyphs);
        Assert.Null(Row("20300108/Server-2030-01-08/WCSCMD.txt", 3).FlagToolTip);

        Assert.Equal(
            [EventFlags.SameTimestamp, EventFlags.TimeBackward, EventFlags.TimeInvalid, EventFlags.UnknownKind, EventFlags.EncodingError],
            _vm.FlagLegend.Select(f => f.Flag));
        Assert.All(_vm.Events.SelectMany(r => r.Flags), f => Assert.Contains(f, _vm.FlagLegend));
    }

    [Fact]
    public async Task 編碼錯誤以替代字元顯示_詳細資訊列出旗標()
    {
        await ImportParsingCases();

        _vm.SelectedEvent = Row(PlcArchive, 5);

        Assert.Equal("\uFFFD", _vm.SelectedEvent.FlagGlyphs);
        Assert.Equal("[A-01][Unload] => 卸貨\uFFFD\uFFFD", _vm.SelectedEvent.Message);
        Assert.Contains("旗標：\uFFFD 編碼錯誤", _vm.SelectedDetail);
    }

    [Fact]
    public async Task 選取事件時_原始LOG面板依原始行序顯示該檔案並標出事件的行()
    {
        await ImportParsingCases();

        _vm.SelectedEvent = Row(PlcCurrent, 5);

        Assert.Equal(PlcCurrent, _vm.RawFile);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], _vm.RawLines.Select(l => l.LineNo)); // 時間倒退的第 1 行維持原位
        Assert.Equal([5, 6], _vm.RawLines.Where(l => l.InSelectedEvent).Select(l => l.LineNo));
        Assert.NotNull(_vm.RawSelectedLine);
        Assert.Equal(5, _vm.RawSelectedLine.LineNo);
        Assert.StartsWith("2030-01-08 08:00:03.0000 | INFO | PLCLOG | [A-03][Unload]", _vm.RawSelectedLine.Text);
    }

    [Fact]
    public async Task 選取另一個檔案的事件時_原始LOG面板跟著切換()
    {
        await ImportParsingCases();
        _vm.SelectedEvent = Row(PlcCurrent, 5);

        _vm.SelectedEvent = Row(PlcArchive, 3);

        Assert.Equal(PlcArchive, _vm.RawFile);
        Assert.Equal(3, _vm.RawSelectedLine?.LineNo);
        Assert.Equal("# 虛構前置內容：檔案開頭沒有時間戳記", _vm.RawLines[0].Text);

        _vm.SelectedEvent = null;
        Assert.Null(_vm.RawFile);
        Assert.Empty(_vm.RawLines);
    }

    [Fact]
    public async Task 混合與分軌檢視可以切換_選取的事件保持不變()
    {
        await ImportParsingCases();
        var selected = Row(PlcCurrent, 5);
        _vm.SelectedEvent = selected;
        Assert.False(_vm.IsTrackView); // 預設為混合檢視

        _vm.IsTrackView = true;

        Assert.Same(selected, _vm.SelectedEvent);
        Assert.Equal(PlcCurrent, _vm.RawFile);
        Assert.Equal(["PLC", "Server"], _vm.Modules); // 依模組順序 EXE → PLC → Server，只列出案件中有的
        Assert.Equal(selected.Message, selected["PLC"]);
        Assert.Equal("", selected["Server"]);

        _vm.IsTrackView = false;
        Assert.Same(selected, _vm.SelectedEvent);
    }

    [Fact]
    public async Task 混合檢視以模組順序決定底色()
    {
        await ImportParsingCases();

        Assert.All(_vm.Events, r => Assert.Equal(_vm.Modules.IndexOf(r.Module), r.ModuleIndex));
    }
}
