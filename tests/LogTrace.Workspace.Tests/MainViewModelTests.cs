using LogTrace.Tests.Support;

namespace LogTrace.Workspace.Tests;

// 不開視窗，以真實的案件引擎驅動（測試資料：tests/fixtures，虛構）
public sealed class MainViewModelTests : IDisposable
{
    readonly TempFolder _tmp = new();
    readonly FakeUserInteraction _ui = new();
    readonly MainViewModel _vm;

    public MainViewModelTests() =>
        _vm = new MainViewModel(new SettingsStore(Path.Combine(_tmp.Path, "settings.json")), _ui, Path.Combine(_tmp.Path, "Cases"), _ => false);

    public void Dispose()
    {
        _vm.Dispose();
        _tmp.Dispose();
    }

    [Fact]
    public void 未建立案件時不能匯入_輸入名稱後可以建立案件()
    {
        Assert.False(_vm.CreateCaseCommand.CanExecute(null));
        _vm.ImportSource = TempFolder.Fixture("one-day-plc");
        Assert.False(_vm.ImportCommand.CanExecute(null));

        _vm.NewCaseName = "案件一";
        _vm.CreateCaseCommand.Execute(null);

        Assert.Equal("案件一", _vm.CurrentCaseName);
        Assert.True(Directory.Exists(_vm.CurrentCaseFolder));
        Assert.True(_vm.ImportCommand.CanExecute(null));
    }

    [Fact]
    public async Task 匯入後_事件清單依事件序號列出並選取第一筆()
    {
        _vm.NewCaseName = "案件一";
        _vm.CreateCaseCommand.Execute(null);
        _vm.ImportSource = TempFolder.Fixture("one-day-plc");

        await _vm.ImportCommand.ExecuteAsync(null);

        Assert.Equal([1L, 2, 3, 4, 5], _vm.Events.Select(e => e.Sequence));
        Assert.Equal("[A-02][Station_Status_Change] => 在席 = OFF", _vm.Events[0].Message);
        Assert.Same(_vm.Events[0], _vm.SelectedEvent);
        Assert.Contains("已匯入 1 個檔案（新增 1）", _vm.Status);
        Assert.Contains("案件共 5 筆事件", _vm.Status);
        Assert.False(_vm.IsBusy);
    }

    [Fact]
    public async Task 選取事件後_顯示原始檔案與行號()
    {
        _vm.NewCaseName = "案件一";
        _vm.CreateCaseCommand.Execute(null);
        _vm.ImportSource = TempFolder.Fixture("one-day-plc");
        await _vm.ImportCommand.ExecuteAsync(null);

        _vm.SelectedEvent = _vm.Events.Single(e => e.Sequence == 4);

        Assert.Equal("[A-01][Unload] => 卸貨完成 CMD_NO=00010001 …", _vm.SelectedEvent.Message);
        Assert.Equal("20300107/PLC-2030-01-07/PLCLOG.txt 第 7～9 行", _vm.SelectedEvent.Source);
        Assert.Contains("來源：20300107/PLC-2030-01-07/PLCLOG.txt 第 7～9 行", _vm.SelectedDetail);
        Assert.Contains("識別碼：PLC/20300107/20300107/PLC-2030-01-07/PLCLOG.txt#7", _vm.SelectedDetail);
        Assert.Contains("詳細：虛構延續行二", _vm.SelectedDetail);
    }

    [Fact]
    public async Task 匯入失敗時_顯示原因且事件清單維持空白()
    {
        _vm.NewCaseName = "案件一";
        _vm.CreateCaseCommand.Execute(null);
        _vm.ImportSource = Path.Combine(_tmp.Path, "不存在的資料夾");

        await _vm.ImportCommand.ExecuteAsync(null);

        Assert.StartsWith("匯入失敗：", _vm.Status);
        Assert.Empty(_vm.Events);
        Assert.Null(_vm.SelectedEvent);
    }

    [Fact]
    public void 瀏覽選取資料夾或壓縮檔後_填入匯入來源_取消時不變()
    {
        _vm.ImportSource = "原本";
        _vm.BrowseImportFolderCommand.Execute(null);
        _vm.BrowseImportArchiveCommand.Execute(null);
        Assert.Equal("原本", _vm.ImportSource);

        _ui.FolderResult = @"D:\虛構\logs";
        _vm.BrowseImportFolderCommand.Execute(null);
        Assert.Equal(@"D:\虛構\logs", _vm.ImportSource);

        _ui.ArchiveResult = @"D:\虛構\logs.7z";
        _vm.BrowseImportArchiveCommand.Execute(null);
        Assert.Equal(@"D:\虛構\logs.7z", _vm.ImportSource);
    }
}
