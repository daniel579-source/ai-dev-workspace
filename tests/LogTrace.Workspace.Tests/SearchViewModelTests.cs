using LogTrace.Tests.Support;

namespace LogTrace.Workspace.Tests;

// 搜尋的畫面邏輯（ADR-0010）。測試資料：tests/fixtures/search-cases（虛構）
public sealed class SearchViewModelTests : IDisposable
{
    readonly TempFolder _tmp = new();
    readonly string _settingsPath;
    readonly MainViewModel _vm;

    public SearchViewModelTests()
    {
        _settingsPath = Path.Combine(_tmp.Path, "settings.json");
        _vm = NewViewModel();
    }

    MainViewModel NewViewModel() =>
        new(new SettingsStore(_settingsPath), new FakeUserInteraction(), Path.Combine(_tmp.Path, "Cases"), _ => false);

    public void Dispose()
    {
        _vm.Dispose();
        _tmp.Dispose();
    }

    async Task Open()
    {
        _vm.NewCaseName = "搜尋";
        _vm.CreateCaseCommand.Execute(null);
        _vm.ImportSource = TempFolder.Fixture("search-cases");
        await _vm.ImportCommand.ExecuteAsync(null);
    }

    List<string> Shown() => _vm.Events.Select(e => e.Event.Message.Split('\n')[0]).ToList();

    [Fact]
    public async Task 開啟案件後才能搜尋_關閉後不能()
    {
        Assert.False(_vm.SearchCommand.CanExecute(null));
        await Open();
        Assert.True(_vm.SearchCommand.CanExecute(null));
        _vm.CloseCaseCommand.Execute(null);
        Assert.False(_vm.SearchCommand.CanExecute(null));
    }

    [Fact]
    public async Task 三個輸入框_只有按搜尋才查詢()
    {
        await Open();

        _vm.SearchAllOf = "卸貨";
        Assert.Equal(12, _vm.Events.Count); // 輸入時不查詢

        _vm.SearchNoneOf = "OK";
        await _vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal(["[A-01] 卸貨完成 CMD_NO=00050001", "System.Exception: 虛構例外"], Shown());
        Assert.StartsWith("符合 2 筆（共 12 筆", _vm.SearchStatus);

        (_vm.SearchAllOf, _vm.SearchNoneOf, _vm.SearchAnyOf) = ("", "", "入庫 heartbeat");
        await _vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal(["[A-02] 入庫要求", "bcr01 heartbeat"], Shown());
    }

    [Fact]
    public async Task 篩選_模組_Logger_等級_旗標_時間區間()
    {
        await Open();
        Assert.Equal(["EXE", "PLC"], _vm.ModuleChoices.Select(m => m.Name).Order());
        Assert.Equal(["DEBUG", "ERROR", "INFO", "WARN"], _vm.LevelChoices.Select(m => m.Name));

        _vm.SearchAllOf = "卸貨";
        _vm.ModuleChoices.Single(m => m.Name == "EXE").IsSelected = true;
        await _vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal(3, _vm.Events.Count);

        _vm.LevelChoices.Single(m => m.Name == "ERROR").IsSelected = true;
        await _vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal(["System.Exception: 虛構例外"], Shown());

        _vm.LevelChoices.Single(m => m.Name == "ERROR").IsSelected = false;
        _vm.SearchFrom = "2030-01-13";
        await _vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal(["卸貨 回報 OK 第二天"], Shown());

        (_vm.SearchAllOf, _vm.SearchFrom) = ("", "");
        _vm.ModuleChoices.Single(m => m.Name == "EXE").IsSelected = false;
        _vm.FlagChoices.Single(f => f.Label.Contains("時間倒退")).IsSelected = true;
        await _vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal(["時間倒退的虛構紀錄"], Shown());

        _vm.SearchTo = "明天";
        await _vm.SearchCommand.ExecuteAsync(null);
        Assert.Contains("時間的格式不對", _vm.SearchStatus);
    }

    [Fact]
    public async Task 顯示全部並標示符合的_CtrlArrow在符合的事件之間移動()
    {
        await Open();
        _vm.SearchAllOf = "bcr01";
        await _vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal(3, _vm.Events.Count);

        _vm.ShowAllWithMarks = true;
        Assert.Equal(12, _vm.Events.Count);
        Assert.Equal(3, _vm.Events.Count(e => e.IsMatch));

        _vm.SelectedEvent = _vm.Events[0];
        _vm.NextMatchCommand.Execute(null);
        Assert.Contains("ＢＣＲ０１", _vm.SelectedEvent!.Event.Message);
        _vm.NextMatchCommand.Execute(null);
        Assert.Equal("bcr01 heartbeat", _vm.SelectedEvent!.Event.Message);
        _vm.PreviousMatchCommand.Execute(null);
        Assert.Contains("ＢＣＲ０１", _vm.SelectedEvent!.Event.Message);

        _vm.ShowAllWithMarks = false;
        Assert.Equal(3, _vm.Events.Count);
        Assert.Contains("ＢＣＲ０１", _vm.SelectedEvent!.Event.Message); // 選取的事件仍在結果中，保持選取
    }

    [Fact]
    public async Task 醒目標示與原文對應_符合處在延續行時標示行號()
    {
        await Open();
        _vm.SearchAllOf = "bcr01";
        await _vm.SearchCommand.ExecuteAsync(null);

        var row = _vm.Events.First();
        Assert.Equal(["[A-03] 條碼讀取失敗 ", "ＢＣＲ０１"], row.MessageSegments.Select(s => s.Text));
        Assert.Equal([false, true], row.MessageSegments.Select(s => s.IsMatch));

        _vm.SearchAllOf = "卸貨逾時";
        await _vm.SearchCommand.ExecuteAsync(null);
        var ex = _vm.Events.Single();
        Assert.Equal($"符合處在第 {ex.Event.Source.LineStart + 1} 行", ex.MatchNote);
        _vm.SelectedEvent = ex;
        Assert.Contains("搜尋：符合處在第", _vm.SelectedDetail);
        Assert.Single(_vm.SelectedDetailSegments, s => s.IsMatch && s.Text == "卸貨逾時");
        Assert.All(ex.MessageSegments, s => Assert.False(s.IsMatch)); // 清單只顯示第一行，第一行沒有符合處
    }

    [Fact]
    public async Task 清除搜尋_顯示全部並取消醒目標示()
    {
        await Open();
        _vm.SearchAllOf = "卸貨";
        await _vm.SearchCommand.ExecuteAsync(null);

        _vm.ClearSearchCommand.Execute(null);

        Assert.Equal(12, _vm.Events.Count);
        Assert.All(_vm.Events, e => Assert.Empty(e.Highlights));
        Assert.Null(_vm.SearchStatus);
        Assert.Equal("卸貨", _vm.SearchAllOf); // 輸入框的內容保留
    }

    [Fact]
    public async Task 電話號碼可以搜尋_顯示時遮罩_點一下才顯示完整號碼()
    {
        await Open();
        _vm.SearchAllOf = "0912-345-678";
        await _vm.SearchCommand.ExecuteAsync(null);
        var row = _vm.Events.Single();

        Assert.Equal("簡訊通知 09xx-xxx-xxx 設備門開啟異常（虛構）", row.Message);
        Assert.Equal(["簡訊通知 ", "09xx-xxx-xxx", " 設備門開啟異常（虛構）"], row.MessageSegments.Select(s => s.Text));
        _vm.SelectedEvent = row;
        Assert.True(_vm.SelectedHasMasked);
        Assert.Contains("09xx-xxx-xxx", _vm.SelectedDetail);
        Assert.DoesNotContain("0912-345-678", _vm.SelectedDetail);
        Assert.Contains("09xx-xxx-xxx", _vm.RawLines.Single(l => l.InSelectedEvent).Text);

        _vm.ToggleRevealCommand.Execute(null);
        Assert.Contains("0912-345-678", _vm.SelectedDetail);
        Assert.Contains("0912-345-678", _vm.RawLines.Single(l => l.InSelectedEvent).Text);

        _vm.SelectedEvent = null;
        _vm.SelectedEvent = row; // 換一筆事件後恢復遮罩
        Assert.False(_vm.RevealSensitive);
        Assert.DoesNotContain("0912-345-678", _vm.SelectedDetail);
    }

    [Fact]
    public async Task 記住最近20次搜尋_依使用者保存_選擇後填回條件()
    {
        await Open();
        for (int i = 0; i < 22; i++)
        {
            _vm.SearchAllOf = $"關鍵字{i}";
            await _vm.SearchCommand.ExecuteAsync(null);
        }
        _vm.SearchAllOf = "卸貨";
        _vm.SearchCaseSensitive = true;
        _vm.ModuleChoices.Single(m => m.Name == "PLC").IsSelected = true;
        await _vm.SearchCommand.ExecuteAsync(null);
        await _vm.SearchCommand.ExecuteAsync(null); // 相同的搜尋不重複記錄

        Assert.Equal(20, _vm.RecentSearches.Count);
        Assert.Equal("全部：卸貨　＋篩選　區分大小寫", _vm.RecentSearches[0].Label);

        using var reopened = NewViewModel();
        Assert.Equal(20, reopened.RecentSearches.Count);

        var shownBefore = Shown();
        (_vm.SearchAllOf, _vm.SearchCaseSensitive) = ("", false);
        _vm.ModuleChoices.Single(m => m.Name == "PLC").IsSelected = false;
        _vm.SelectedRecentSearch = _vm.RecentSearches[0];
        Assert.Equal(("卸貨", true), (_vm.SearchAllOf, _vm.SearchCaseSensitive));
        Assert.True(_vm.ModuleChoices.Single(m => m.Name == "PLC").IsSelected);
        Assert.Equal(shownBefore, Shown()); // 選擇最近的搜尋只填回條件，不會立即查詢
    }
}
