using LogTrace.Engine.Cases;
using LogTrace.Engine.Events;
using LogTrace.Engine.Parsing;
using LogTrace.Engine.Plants;
using LogTrace.Tests.Support;

namespace LogTrace.Engine.Tests;

// 解析欄位（ADR-0005 第 8 點）。測試資料：tests/fixtures/fields-cases（虛構）
public sealed class ParsedFieldsTests : IDisposable
{
    readonly TempFolder _tmp = new();
    readonly CaseLibrary _library;

    public ParsedFieldsTests() => _library = new CaseLibrary(Path.Combine(_tmp.Path, "Cases"));

    public void Dispose() => _tmp.Dispose();

    /// <summary>初始規則沒有涵蓋的欄位（格式待確認），以虛構的格式與自訂規則測試。</summary>
    static readonly ParseRule[] CustomRules =
    [
        new("對接設備", null, null, @"(?<dock>EQ[0-9]{3})"),
        new("桶", null, null, @"(?<bin>B[0-9]{3})"),
        new("命令內容", null, "WCSDebug", @"命令=(?<command>[0-9]{8}) 類型=(?<commandType>[A-Z]) 來源=(?<source>[0-9]+) 目的地=(?<destination>[0-9]+)"),
        new("儲位", null, "WCSDebug", @"目的地=(?<storage>[0-9]{7})(?![0-9])"),
        new("警報", null, null, @"ALARM (?<alarm>發生|復歸) 設備=(?<vehicle>[A-Z]{4}[0-9]{2}) 代碼=(?<alarmCode>[A-Z0-9]+)"),
        new("連線與模式", "Server", null, @"(?<vehicle>[A-Z]{4}[0-9]{2}) (?<online>ON-LINE|OFF-LINE) (?<mode>自動|手動)"),
        new("站點模式", null, null, @"站點 (?<station>[0-9]{4}) 模式=(?<stationMode>[0-9])"),
    ];

    static PlantConfig WithRules(IEnumerable<ParseRule> rules) => PlantConfig.Default with { ParseRules = [.. rules] };

    static PlantConfig Full => WithRules([.. PlantConfig.DefaultParseRules, .. CustomRules]);

    Case Import(PlantConfig plant)
    {
        var c = _library.Create("欄位", plant);
        c.Import(TempFolder.Fixture("fields-cases"));
        return c;
    }

    static LogEvent EventWith(Case c, string text) => c.GetEvents(1, int.MaxValue).Single(e => e.Message.Contains(text));

    static List<(string, string)> Fields(Case c, string text) =>
        c.GetFields(EventWith(c, text).Sequence).Select(f => (f.Kind, f.Value)).ToList();

    [Fact]
    public void 初始規則_站點狀態變化_站點_訊號與ON_OFF()
    {
        using var c = Import(PlantConfig.Default);

        Assert.Equal([(FieldKinds.Station, "A-01"), (FieldKinds.Signal, "在席"), (FieldKinds.SignalValue, "ON")],
            Fields(c, "在席 = ON"));
    }

    [Fact]
    public void 初始規則_站點與設備的組合_命令編號()
    {
        using var c = Import(PlantConfig.Default);

        var fields = Fields(c, "命令編號 = 00030001");

        Assert.Contains((FieldKinds.Station, "A-01"), fields);
        Assert.Contains((FieldKinds.Vehicle, "SRCA01"), fields);
        Assert.Contains((FieldKinds.Command, "00030001"), fields);
    }

    [Fact]
    public void 初始規則_棧板命令_一筆事件有多個欄位()
    {
        using var c = Import(PlantConfig.Default);

        Assert.Equal([(FieldKinds.Command, "00030001"), (FieldKinds.Station, "A-02"), (FieldKinds.Pallet, "123456789")],
            Fields(c, "卸貨完成"));
    }

    [Fact]
    public void 初始規則_其他模組中的設備與命令編號()
    {
        using var c = Import(PlantConfig.Default);

        Assert.Equal([(FieldKinds.Command, "00030002"), (FieldKinds.Vehicle, "SRCA02")], Fields(c, "SubmitCompleted"));
    }

    [Fact]
    public void 自訂規則_對接設備與多個桶()
    {
        using var c = Import(Full);

        Assert.Equal([(FieldKinds.Dock, "EQ901"), (FieldKinds.Bin, "B001"), (FieldKinds.Bin, "B002")], Fields(c, "交給對接設備"));
    }

    [Fact]
    public void 自訂規則_命令類型_來源_目的地_儲位_只套用到指定的Logger()
    {
        using var c = Import(Full);

        var fields = Fields(c, "路徑規劃");

        Assert.Contains((FieldKinds.CommandType, "S"), fields);
        Assert.Contains((FieldKinds.Source, "1001"), fields);
        Assert.Contains((FieldKinds.Destination, "0100802"), fields);
        Assert.Contains((FieldKinds.Storage, "0100802"), fields);
        Assert.Single(fields, f => f == (FieldKinds.Command, "00030002")); // 兩條規則都抽到，只記一次
    }

    [Fact]
    public void 自訂規則_警報發生與復歸_警報代碼()
    {
        using var c = Import(Full);

        Assert.Contains((FieldKinds.Alarm, "發生"), Fields(c, "ALARM 發生"));
        Assert.Contains((FieldKinds.Alarm, "復歸"), Fields(c, "ALARM 復歸"));
        Assert.Contains((FieldKinds.AlarmCode, "E123"), Fields(c, "ALARM 復歸"));
        Assert.Contains((FieldKinds.Vehicle, "SRCA02"), Fields(c, "ALARM 發生"));
    }

    [Fact]
    public void 自訂規則_連線與自動手動_只套用到指定的模組()
    {
        using var c = Import(Full);

        Assert.Contains((FieldKinds.Online, "ON-LINE"), Fields(c, "ON-LINE"));
        Assert.Contains((FieldKinds.Mode, "自動"), Fields(c, "ON-LINE"));
    }

    [Fact]
    public void 自訂規則_站點模式與數字型站點()
    {
        using var c = Import(Full);

        Assert.Equal([(FieldKinds.Station, "1001"), (FieldKinds.StationMode, "2")], Fields(c, "模式=2"));
    }

    [Fact]
    public void 依元素的所有別名篩選事件_不分大小寫()
    {
        using var c = Import(Full);

        // 一個站點元素有兩個別名：PLC 的 A-01 與 WCS 的 1001（虛構）
        var seqs = c.FindSequences(FieldKinds.Identifiers, ["a-01", "1001"]);

        var messages = c.GetEvents(1, int.MaxValue).Where(e => seqs.Contains(e.Sequence)).Select(e => e.Message).ToList();
        Assert.Equal(3, messages.Count);
        Assert.Contains(messages, m => m.Contains("在席 = ON"));
        Assert.Contains(messages, m => m.Contains("命令編號 = 00030001"));
        Assert.Contains(messages, m => m.Contains("模式=2"));
        Assert.Equal(seqs.Order(), seqs); // 依時間排序
    }

    [Theory]
    [InlineData(FieldKinds.Command, "00030001", 2)]
    [InlineData(FieldKinds.Pallet, "123456789", 1)]
    [InlineData(FieldKinds.Bin, "b002", 1)]
    [InlineData(FieldKinds.Command, "99999999", 0)]
    public void 依命令_棧板_桶編號篩選事件(string kind, string value, int expected)
    {
        using var c = Import(Full);

        Assert.Equal(expected, c.FindSequences([kind], [value]).Count);
    }

    [Fact]
    public void 案件中的站點_搬送機_對接設備編號與出現次數()
    {
        using var c = Import(Full);

        var counts = c.GetIdentifierCounts();

        Assert.Contains((FieldKinds.Station, "A-01", 2L), counts);
        Assert.Contains((FieldKinds.Station, "A-09", 1L), counts);
        Assert.Contains((FieldKinds.Vehicle, "SRCA02", 4L), counts);
        Assert.Contains((FieldKinds.Dock, "EQ901", 1L), counts);
        Assert.DoesNotContain(counts, x => x.Kind == FieldKinds.Command);
    }

    [Fact]
    public void 修改規則後重新解析_結果隨之改變_規則存進案件並記錄稽核()
    {
        string folder;
        using (var c = Import(PlantConfig.Default))
        {
            Assert.DoesNotContain(c.GetIdentifierCounts(), x => x.Kind == FieldKinds.Dock);

            c.ReparseFields([.. PlantConfig.DefaultParseRules, CustomRules[0]]);

            Assert.Contains((FieldKinds.Dock, "EQ901", 1L), c.GetIdentifierCounts());
            Assert.Equal("重新解析欄位", c.GetAudit()[^1].Action);
            folder = c.Folder;
        }
        using var reopened = Case.Open(folder);
        Assert.Equal(PlantConfig.DefaultParseRules.Count + 1, reopened.Info.Plant.ParseRules.Count);
    }

    [Fact]
    public void 規則有誤時不重新解析()
    {
        using var c = Import(PlantConfig.Default);
        var before = c.GetIdentifierCounts();

        Assert.Throws<ArgumentException>(() => c.ReparseFields([new ParseRule("壞掉", null, null, "(?<station>[")]));

        Assert.Equal(before, c.GetIdentifierCounts());
    }

    [Theory]
    [InlineData("(?<station>[", "正規表示式有誤")]
    [InlineData("(?<spaceship>X)", "不認得的欄位")]
    [InlineData("A-[0-9]{2}", "沒有任何欄位")]
    public void 規則檢查(string pattern, string expected)
    {
        Assert.Contains(expected, FieldExtractor.Check(new ParseRule("測試", null, null, pattern)));
    }

    [Fact]
    public void 廠別設定檔_解析規則的讀寫往返_新設定從初始規則開始_有誤時無法存檔()
    {
        var doc = new PlantConfigDocument { Plant = "範例廠（虛構）" };
        Assert.Equal(PlantConfig.DefaultParseRules, doc.ParseRules);
        doc.ParseRules.Add(CustomRules[2]);
        var path = Path.Combine(_tmp.Path, "規則.json");

        PlantConfigFile.Save(doc, path);
        var loaded = PlantConfigFile.Load(path);

        Assert.Equal(doc.ParseRules, loaded.ParseRules);
        Assert.Equal(doc.ParseRules, loaded.ToPlantConfig().ParseRules);

        loaded.ParseRules.Add(new ParseRule("壞掉", null, null, "(?<bogus>x)"));
        Assert.Throws<PlantConfigValidationException>(() => PlantConfigFile.Save(loaded, path));
    }
}
