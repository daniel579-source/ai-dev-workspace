using LogTrace.Tests.Support;

namespace LogTrace.Workspace.Tests;

// 全文索引的背景建立、正規表示式、搜尋的進度與取消（#21）。測試資料：tests/fixtures/search-cases（虛構），以及測試中產生的虛構 LOG。
public sealed class SearchIndexViewModelTests : IDisposable
{
    readonly TempFolder _tmp = new();
    readonly MainViewModel _vm;

    public SearchIndexViewModelTests() =>
        _vm = new MainViewModel(new SettingsStore(Path.Combine(_tmp.Path, "settings.json")), new FakeUserInteraction(), Path.Combine(_tmp.Path, "Cases"), _ => false)
        {
            IndexDiskLimitRatio = 10_000, // 測試資料很小，SQLite 的固定開銷就超過 5 倍
        };

    public void Dispose()
    {
        _vm.Dispose();
        _tmp.Dispose();
    }

    async Task Open(string source)
    {
        _vm.NewCaseName = "索引";
        _vm.CreateCaseCommand.Execute(null);
        _vm.ImportSource = source;
        await _vm.ImportCommand.ExecuteAsync(null);
        await _vm.WaitForIndexAsync();
    }

    [Fact]
    public async Task 開啟案件後在背景建立索引_顯示狀態_搜尋說明使用的方式()
    {
        await Open(TempFolder.Fixture("search-cases"));

        Assert.Equal("全文索引：已建立（2 天）", _vm.IndexStatus);
        Assert.False(_vm.IsIndexing);

        _vm.SearchAllOf = "heartbeat";
        await _vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal(1, _vm.MatchCount);
        Assert.EndsWith("全文索引）", _vm.SearchStatus);

        _vm.SearchAllOf = "卸貨"; // 2 個字：掃描
        await _vm.SearchCommand.ExecuteAsync(null);
        Assert.EndsWith("掃描）", _vm.SearchStatus);
    }

    [Fact]
    public async Task 重建索引_關閉案件後不顯示狀態()
    {
        await Open(TempFolder.Fixture("search-cases"));

        _vm.RebuildIndexCommand.Execute(null);
        await _vm.WaitForIndexAsync();
        Assert.Equal("全文索引：已建立（2 天）", _vm.IndexStatus);

        _vm.CloseCaseCommand.Execute(null);
        Assert.Null(_vm.IndexStatus);
        Assert.False(_vm.RebuildIndexCommand.CanExecute(null));
    }

    [Fact]
    public async Task 磁碟用量上限_不建索引_說明以掃描搜尋()
    {
        using var vm = new MainViewModel(new SettingsStore(Path.Combine(_tmp.Path, "s2.json")), new FakeUserInteraction(), Path.Combine(_tmp.Path, "Cases2"), _ => false);
        vm.NewCaseName = "上限";
        vm.CreateCaseCommand.Execute(null);
        vm.ImportSource = TempFolder.Fixture("search-cases");
        await vm.ImportCommand.ExecuteAsync(null);
        await vm.WaitForIndexAsync();

        Assert.Equal("全文索引：案件磁碟用量會超過原始 LOG 的 5 倍，已建立 0/2 天，其餘以掃描搜尋", vm.IndexStatus);
    }

    [Fact]
    public async Task 正規表示式_預設關閉_語法有誤時提示並保留之前的結果_記住在最近的搜尋()
    {
        await Open(TempFolder.Fixture("search-cases"));
        Assert.False(_vm.SearchRegex);

        _vm.SearchRegex = true;
        _vm.SearchAllOf = @"bcr\d1";
        await _vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal(3, _vm.MatchCount);
        Assert.Contains(_vm.Events, r => r.Highlights.Count > 0);

        _vm.SearchAllOf = "bcr(01";
        await _vm.SearchCommand.ExecuteAsync(null);
        Assert.StartsWith("正規表示式「bcr(01」有誤", _vm.SearchStatus);
        Assert.Equal(3, _vm.MatchCount); // 之前的結果不變

        Assert.Equal(@"全部：bcr\d1　正規表示式", _vm.RecentSearches[0].Label);
        _vm.SearchRegex = false;
        _vm.SelectedRecentSearch = _vm.RecentSearches[0];
        Assert.True(_vm.SearchRegex);
    }

    [Fact]
    public async Task 搜尋可以取消_取消後畫面維持可用()
    {
        // 虛構 LOG：一長串 a，搭配會大量回溯的正規表示式，一筆就要比對到上限（2 秒）
        var day = Directory.CreateDirectory(Path.Combine(_tmp.Path, "slow", "20300107", "EXE-2030-01-07")).FullName;
        File.WriteAllLines(Path.Combine(day, "EXEDebug.txt"),
            Enumerable.Range(0, 8).Select(i => $"2030-01-07 09:00:0{i}.0000 | DEBUG | EXEDebug | {new string('a', 40)}! 虛構"));
        await Open(Path.Combine(_tmp.Path, "slow"));

        _vm.SearchRegex = true;
        _vm.SearchAllOf = "(a+)+b";
        var search = _vm.SearchCommand.ExecuteAsync(null);
        Assert.True(_vm.IsSearching);
        Assert.True(_vm.CancelSearchCommand.CanExecute(null));
        _vm.CancelSearchCommand.Execute(null);
        await search;

        Assert.Equal("已取消搜尋。", _vm.SearchStatus);
        Assert.False(_vm.IsSearching);
        Assert.False(_vm.IsBusy);

        _vm.SearchRegex = false;
        _vm.SearchAllOf = "虛構";
        await _vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal(8, _vm.MatchCount);
    }
}
