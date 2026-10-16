using LogTrace.Engine.Cases;
using LogTrace.Engine.Events;
using LogTrace.Engine.Parsing;
using LogTrace.Engine.Plants;
using LogTrace.Engine.Search;
using LogTrace.Engine.Storage;
using LogTrace.Tests.Support;

namespace LogTrace.Engine.Tests;

// 全文索引與正規表示式（#21，ADR-0010 第 2 點、ADR-0011 第 1、2、4、6 點）。
// 測試資料：tests/fixtures/search-cases（虛構，2030-01-12、13 兩天）。
public sealed class SearchIndexTests : IDisposable
{
    readonly TempFolder _tmp = new();
    readonly Case _case;

    public SearchIndexTests()
    {
        _case = new CaseLibrary(Path.Combine(_tmp.Path, "Cases")).Create("索引", PlantConfig.Default);
        _case.Import(TempFolder.Fixture("search-cases"));
        _case.Index.DiskLimitRatio = 10_000; // 測試資料很小，SQLite 的固定開銷就超過 5 倍
    }

    public void Dispose()
    {
        _case.Dispose();
        _tmp.Dispose();
    }

    /// <summary>涵蓋各種條件的查詢（與 SearchTests 相同的關鍵字，另加上會用到索引的組合）。</summary>
    static readonly string[] Specs =
    [
        "all:卸貨", "all:卸貨逾時", "all:bcr01", "all:ＢＣＲ０１", "all:Bcr01|case", "all:cr01|none:BCR01|case", "all:cr01|none:BCR01",
        "any:入庫 heartbeat", "all:卸貨|none:OK", "all:卸貨 回報|any:ok 不存在", "all:\"door open\"", "all:door open",
        "all:heartbeat", "any:heartbeat restart", "any:heartbeat 卸貨", "all:0912-345-678", "all:CMD_NO=00050001", "all:不存在的字串",
        "none:卸貨", "all:虛構例外", "all:exception|module:EXE", "all:heartbeat|from:2030-01-13",
    ];

    public static TheoryData<string> Queries => new(Specs);

    static SearchQuery Parse(string spec)
    {
        var q = new SearchQuery();
        foreach (var part in spec.Split('|'))
        {
            int colon = part.IndexOf(':');
            var (key, value) = colon < 0 ? (part, "") : (part[..colon], part[(colon + 1)..]);
            q = key switch
            {
                "all" => q with { AllOf = value },
                "any" => q with { AnyOf = value },
                "none" => q with { NoneOf = value },
                "case" => q with { CaseSensitive = true },
                "module" => q with { Modules = [value] },
                "from" => q with { From = DateTime.Parse(value, System.Globalization.CultureInfo.InvariantCulture) },
                _ => throw new ArgumentException(key),
            };
        }
        return q;
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public void 同一個查詢走索引與走掃描_結果完全相同(string spec)
    {
        var q = Parse(spec);
        var scan = _case.Search(q, SearchPath.ScanOnly, null);
        _case.BuildIndex();

        var auto = _case.Search(q, SearchPath.Auto, null);

        Assert.Equal(scan.Sequences, auto.Sequences);
        Assert.Equal(0, scan.IndexedDays);
    }

    [Fact]
    public void 索引建好後_罕見的3字以上關鍵字用索引_1到2字與只有不包含的查詢用掃描()
    {
        Assert.Equal((0, 2), _case.IndexState());
        Assert.Equal(0, _case.Search(new SearchQuery { AllOf = "heartbeat" }, SearchPath.Auto, null).IndexedDays); // 尚未建立：掃描

        var result = _case.BuildIndex();

        Assert.Equal((2, 2, false), (result.BuiltDays, result.TotalDays, result.LimitReached));
        Assert.Equal((2, 2), _case.IndexState());
        Assert.Equal((2, 0), Route(new SearchQuery { AllOf = "heartbeat" }));
        Assert.Equal((0, 2), Route(new SearchQuery { AllOf = "卸貨" }));          // 2 個字
        Assert.Equal((0, 2), Route(new SearchQuery { NoneOf = "heartbeat" }));    // 只有「不包含」
        Assert.Equal((0, 2), Route(new SearchQuery { AllOf = "bcr.1", Regex = true }));
        Assert.Equal((2, 0), Route(new SearchQuery { AllOf = "卸貨 heartbeat" })); // 選可以用索引的那一個
        Assert.True(_case.Search(new SearchQuery { AllOf = "heartbeat", FieldKinds = [FieldKinds.Station], FieldValues = ["A-01"] },
            SearchPath.Auto, null).ByFields); // 有精確篩選：先用參照表
    }

    (int Indexed, int Scanned) Route(SearchQuery q) => _case.Search(q, SearchPath.Auto, null) is var r ? (r.IndexedDays, r.ScannedDays) : default;

    [Fact]
    public void 刪除索引後可以重新建立_結果不變()
    {
        _case.BuildIndex();
        var before = Specs.Select(q => _case.Search(Parse(q)).ToList()).ToList();

        _case.DeleteIndex();
        Assert.False(Directory.Exists(_case.Index.Folder));
        Assert.Equal((0, 2), _case.IndexState());
        Assert.Equal(before, Specs.Select(q => _case.Search(Parse(q)).ToList()).ToList()); // 沒有索引：掃描

        _case.BuildIndex();
        Assert.Equal((2, 2), _case.IndexState());
        Assert.Equal(before, Specs.Select(q => _case.Search(Parse(q)).ToList()).ToList());
    }

    [Fact]
    public async Task 索引建立期間可以照常搜尋_結果與掃描相同()
    {
        var q = new SearchQuery { AllOf = "heartbeat" };
        var expected = _case.Search(q, SearchPath.ScanOnly, null).Sequences;
        var progress = new List<IndexBuildProgress>();

        var build = Task.Run(() => _case.BuildIndex(new SyncProgress<IndexBuildProgress>(progress.Add)));
        int searches = 0;
        do
        {
            Assert.Equal(expected, _case.Search(q, SearchPath.Auto, null).Sequences);
            searches++;
        } while (!build.IsCompleted);
        await build;

        Assert.True(searches > 0);
        Assert.Equal(new IndexBuildProgress(2, 2, null), progress[^1]);
        Assert.Contains(progress, p => p.Day == "20300112");
    }

    [Fact]
    public void 案件內容改變後_舊的索引不再使用_重建後恢復()
    {
        _case.BuildIndex();
        var q = new SearchQuery { AllOf = "heartbeat" };
        var before = _case.Search(q);

        _case.SetTimeCorrection(TimeCorrection.Create(TimeSpan.FromSeconds(5), []));

        Assert.Equal((0, 2), _case.IndexState()); // 內容版本改變
        Assert.Equal((0, 2), Route(q));
        Assert.Equal(before, _case.Search(q)); // 整體偏移量不改變排序

        _case.BuildIndex();
        Assert.Equal((2, 0), Route(q));
        Assert.Equal(before, _case.Search(q));
    }

    [Fact]
    public void 磁碟用量會超過原始LOG的5倍時_不建索引_只用掃描()
    {
        _case.Index.DiskLimitRatio = 5; // 這份測試資料的案件早已超過原始 LOG 的 5 倍

        var result = _case.BuildIndex();

        Assert.True(result.LimitReached);
        Assert.Equal(0, result.BuiltDays);
        Assert.Equal((0, 2), Route(new SearchQuery { AllOf = "heartbeat" }));
        Assert.NotEmpty(_case.Search(new SearchQuery { AllOf = "heartbeat" }));
    }

    [Fact]
    public void 正規表示式_比對整筆事件_全形半形相同_不分大小寫可選()
    {
        var events = _case.GetEvents(1, int.MaxValue).ToList();
        List<string> Find(SearchQuery q) => _case.Search(q).Select(s => events.Single(e => e.Sequence == s).Message.Split('\n')[0]).ToList();

        Assert.Equal(["[A-03] 條碼讀取失敗 ＢＣＲ０１", "bcr01 heartbeat", "Bcr01 Restart Done"], Find(new SearchQuery { AllOf = @"bcr\d1", Regex = true }));
        Assert.Equal(["Bcr01 Restart Done"], Find(new SearchQuery { AllOf = @"^Bcr\d+", Regex = true, CaseSensitive = true }));
        Assert.Equal(["front door open detected", "door is open"], Find(new SearchQuery { AllOf = "door.*open", Regex = true }));
        Assert.Equal(["door is open"], Find(new SearchQuery { AllOf = "door.*open", NoneOf = "front", Regex = true }));
        Assert.Contains("System.Exception: 虛構例外", Find(new SearchQuery { AllOf = "Exception:\\s+虛構", Regex = true })); // 延續行也比對

        var q = new SearchQuery { AllOf = @"bcr\d1", Regex = true };
        Assert.Equal([(14, 5)], q.FindMatches("[A-03] 條碼讀取失敗 ＢＣＲ０１")); // 醒目位置對應到原文的全形字
    }

    [Fact]
    public void 正規表示式語法有誤時說明_不執行搜尋()
    {
        var q = new SearchQuery { AllOf = "bcr(01", Regex = true };

        Assert.Contains("正規表示式「bcr(01」有誤", q.Validate());
        var ex = Assert.Throws<ArgumentException>(() => _case.Search(q));
        Assert.Contains("正規表示式「bcr(01」有誤", ex.Message);
        Assert.Null(new SearchQuery { AllOf = "bcr(01" }.Validate()); // 沒有勾選正規表示式時是一般文字
    }

    [Fact]
    public void 搜尋可以取消_回報進度()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => _case.Search(new SearchQuery { AllOf = ".*", Regex = true }, SearchPath.Auto, null, cts.Token));

        var reported = new List<double>();
        _case.Search(new SearchQuery { AllOf = "卸貨" }, SearchPath.Auto, new SyncProgress<double>(reported.Add));
        Assert.Equal(1.0, reported.Max());
    }

    /// <summary>同步回報的 IProgress（Progress&lt;T&gt; 會透過同步內容非同步回報）。</summary>
    sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        readonly object _lock = new();

        public void Report(T value)
        {
            lock (_lock) report(value);
        }
    }
}
