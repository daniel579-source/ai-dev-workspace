using LogTrace.Engine.Cases;
using LogTrace.Engine.Events;
using LogTrace.Engine.Plants;
using LogTrace.Engine.Search;
using LogTrace.Tests.Support;

namespace LogTrace.Engine.Tests;

// 搜尋（ADR-0010）。測試資料：tests/fixtures/search-cases（虛構）。
// 預期結果是依測試資料人工列出的。
public sealed class SearchTests : IDisposable
{
    readonly TempFolder _tmp = new();
    readonly Case _case;
    readonly List<LogEvent> _events;

    public SearchTests()
    {
        _case = new CaseLibrary(Path.Combine(_tmp.Path, "Cases")).Create("搜尋", PlantConfig.Default);
        _case.Import(TempFolder.Fixture("search-cases"));
        _events = _case.GetEvents(1, int.MaxValue).ToList();
    }

    public void Dispose()
    {
        _case.Dispose();
        _tmp.Dispose();
    }

    /// <summary>搜尋結果的訊息第一行（依結果順序）。</summary>
    List<string> Find(SearchQuery q) =>
        _case.Search(q).Select(s => _events.Single(e => e.Sequence == s).Message.Split('\n')[0]).ToList();

    [Fact]
    public void 同一個中文關鍵字在Big5與UTF8檔案中都找得到_也找得到只在延續行的()
    {
        Assert.Equal(
            ["[A-01] 卸貨完成 CMD_NO=00050001", "卸貨 回報 OK", "System.Exception: 虛構例外", "卸貨 回報 OK 第二天"],
            Find(new SearchQuery { AllOf = "卸貨" }));
        Assert.Equal("Big5", _case.GetFiles().Single(f => f.Module == "PLC").Encoding);
    }

    [Fact]
    public void 只出現在延續行的字串_可以找到並標示行號()
    {
        var q = new SearchQuery { AllOf = "卸貨逾時" };
        var e = _events.Single(x => x.Sequence == _case.Search(q).Single());

        var match = q.FindMatches(e.Message).Single();
        int lineInEvent = e.Message[..match.Start].Count(c => c == '\n');

        Assert.Equal(1, lineInEvent);
        Assert.Equal(e.Source.LineStart + 1, e.Source.LineStart + lineInEvent); // 原始檔的第 N 行
        Assert.Equal("卸貨逾時", e.Message.Substring(match.Start, match.Length));
    }

    [Theory]
    [InlineData("bcr01")]
    [InlineData("BCR01")]
    [InlineData("ＢＣＲ０１")]
    [InlineData("Ｂcr０1")]
    public void 不分大小寫_全形與半形視為相同(string term)
    {
        Assert.Equal(["[A-03] 條碼讀取失敗 ＢＣＲ０１", "bcr01 heartbeat", "Bcr01 Restart Done"], Find(new SearchQuery { AllOf = term }));
    }

    [Theory]
    [InlineData("Bcr01", "Bcr01 Restart Done")]
    [InlineData("bcr01", "bcr01 heartbeat")]
    [InlineData("BCR01", "[A-03] 條碼讀取失敗 ＢＣＲ０１")]
    [InlineData("ＢＣＲ０１", "[A-03] 條碼讀取失敗 ＢＣＲ０１")]
    public void 區分大小寫時_全形與半形仍視為相同(string term, string expected)
    {
        Assert.Equal([expected], Find(new SearchQuery { AllOf = term, CaseSensitive = true }));
    }

    [Fact]
    public void 區分大小寫的不包含_只排除大小寫相同的()
    {
        // 全形的 ＢＣＲ０１ 正規化後是大寫的 BCR01，所以不含小寫的 cr01；不區分大小寫時，「不包含 BCR01」會把三筆都排除
        Assert.Equal(["bcr01 heartbeat", "Bcr01 Restart Done"],
            Find(new SearchQuery { AllOf = "cr01", NoneOf = "BCR01", CaseSensitive = true }));
        Assert.Empty(Find(new SearchQuery { AllOf = "cr01", NoneOf = "BCR01" }));
    }

    [Fact]
    public void 三個輸入框_全部包含_任一包含_不包含()
    {
        Assert.Equal(["[A-02] 入庫要求", "bcr01 heartbeat"], Find(new SearchQuery { AnyOf = "入庫 heartbeat" }));
        Assert.Equal(["[A-01] 卸貨完成 CMD_NO=00050001", "System.Exception: 虛構例外"], Find(new SearchQuery { AllOf = "卸貨", NoneOf = "OK" }));
        Assert.Equal(["卸貨 回報 OK", "卸貨 回報 OK 第二天"], Find(new SearchQuery { AllOf = "卸貨 回報", AnyOf = "ok 不存在" }));
    }

    [Fact]
    public void 雙引號表示片語()
    {
        Assert.Equal(["front door open detected"], Find(new SearchQuery { AllOf = "\"door open\"" }));
        Assert.Equal(["front door open detected", "door is open"], Find(new SearchQuery { AllOf = "door open" }));
        Assert.Equal(["door", "open", "a b"], SearchQuery.Terms("door  open \"a b\""));
    }

    [Fact]
    public void 篩選_模組_Logger_等級_時間區間_都是AND()
    {
        Assert.Equal(["卸貨 回報 OK", "System.Exception: 虛構例外", "卸貨 回報 OK 第二天"], Find(new SearchQuery { AllOf = "卸貨", Modules = ["EXE"] }));
        Assert.Equal(["[A-01] 卸貨完成 CMD_NO=00050001"], Find(new SearchQuery { AllOf = "卸貨", Loggers = ["PLCLOG"] }));
        Assert.Equal(["System.Exception: 虛構例外"], Find(new SearchQuery { Levels = ["ERROR"] }));
        Assert.Equal(["卸貨 回報 OK 第二天"], Find(new SearchQuery { AllOf = "卸貨", From = new DateTime(2030, 1, 13) }));
        Assert.Equal(["[A-01] 卸貨完成 CMD_NO=00050001", "卸貨 回報 OK"],
            Find(new SearchQuery { AllOf = "卸貨", From = new DateTime(2030, 1, 12, 8, 0, 0), To = new DateTime(2030, 1, 12, 8, 0, 4) }));
        Assert.Equal(12, Find(new SearchQuery()).Count); // 沒有任何條件：全部
    }

    [Fact]
    public void 篩選_旗標與解析欄位()
    {
        Assert.Equal(["[A-01] 卸貨完成 CMD_NO=00050001"],
            Find(new SearchQuery { FieldKinds = [Parsing.FieldKinds.Station], FieldValues = ["a-01"] }));
        Assert.Equal(["時間倒退的虛構紀錄"], Find(new SearchQuery { Flags = EventFlags.TimeBackward }));
        Assert.Equal(["時間倒退的虛構紀錄"], Find(new SearchQuery { Flags = EventFlags.TimeBackward | EventFlags.EncodingError }));
        Assert.Empty(Find(new SearchQuery { Flags = EventFlags.TimeBackward, AllOf = "卸貨" }));
    }

    [Fact]
    public void 結果依事件序號_與事件清單排序一致_重複查詢順序相同()
    {
        var q = new SearchQuery { AnyOf = "卸貨 bcr01 door" };

        var first = _case.Search(q);
        var second = _case.Search(q);

        Assert.Equal(first, second);
        Assert.Equal(first.Order(), first);
        Assert.Equal(_events.Where(e => q.MatchesText(e.Message)).Select(e => e.Sequence), first); // 掃描與逐筆比對結果相同
    }

    [Fact]
    public void 可以用電話號碼搜尋_顯示時遮罩()
    {
        var found = Find(new SearchQuery { AllOf = "0912-345-678" });
        Assert.Equal(["簡訊通知 0912-345-678 設備門開啟異常（虛構）"], found);

        var masker = new TextMasker(PlantConfig.Default.MaskPatterns);
        Assert.Equal("簡訊通知 09xx-xxx-xxx 設備門開啟異常（虛構）", masker.Mask(found[0]));
        Assert.Equal("09xxxxxxxx", masker.Mask("0912345678"));
        Assert.Equal("CMD_NO=00050001 091234567", masker.Mask("CMD_NO=00050001 091234567")); // 不是手機號碼
    }

    [Fact]
    public void 醒目位置與原文對應正確_正規化會改變長度時也一樣()
    {
        var q = new SearchQuery { AllOf = "bcr01" };
        var message = "[A-03] 條碼讀取失敗 ＢＣＲ０１";
        var m = q.FindMatches(message).Single();
        Assert.Equal("ＢＣＲ０１", message.Substring(m.Start, m.Length));

        // ㈱ 正規化後是「(株)」三個字元：醒目位置仍對應到原文的一個字
        var q2 = new SearchQuery { AllOf = "(株) 測試" };
        var text = "虛構㈱公司 測試";
        Assert.Equal(["㈱", "測試"], q2.FindMatches(text).Select(x => text.Substring(x.Start, x.Length)));
    }

    [Fact]
    public void 正規化_NFKC加大小寫摺疊()
    {
        Assert.Equal("abc123", TextNormalizer.Normalize("ＡＢＣ１２３"));
        Assert.Equal("アラーム", TextNormalizer.Normalize("ｱﾗｰﾑ"));
        Assert.Equal("ABC", TextNormalizer.Normalize("ＡＢＣ", caseSensitive: true));
    }
}
