using LogTrace.Engine.Plants;
using LogTrace.Tests.Support;

namespace LogTrace.Engine.Tests;

// 廠別設定檔（ADR-0006 第 7 點）。內容全部是虛構的。
public sealed class PlantConfigFileTests : IDisposable
{
    readonly TempFolder _tmp = new();

    public void Dispose() => _tmp.Dispose();

    static PlantConfigDocument Sample() => new()
    {
        Plant = "範例廠（虛構）",
        Layout = new LayoutSection
        {
            StorageCodeRule = "RRCCCLL",
            Elements =
            [
                new() { Id = "st-1", Type = LayoutElementType.Station, Name = "入庫口1", Aliases = ["A-01", "1001"],
                        X = 3, Y = 11.5, W = 2, H = 2, Rotation = 0, Height = 1.2, Props = [new("樓層", "1F"), new("備註", "虛構")] },
                new() { Id = "st-2", Type = LayoutElementType.Station, Name = "輸送站2", Aliases = ["A-02"],
                        X = 7.5, Y = 11.5, W = 2, H = 2, Rotation = 90, Height = 1.2 },
                new() { Id = "rk-1", Type = LayoutElementType.Rack, Name = "料架 R01", Row = "01", ColFrom = 1, ColTo = 20,
                        Levels = 5, LevelHeight = 2.5, X = 2, Y = 2, W = 24, H = 1.5, Rotation = 0 },
                new() { Id = "vh-1", Type = LayoutElementType.Vehicle, Name = "SRCA01", Aliases = ["SRCA01"],
                        X = 4, Y = 4.5, W = 2.5, H = 1.2, Rotation = 0, Height = 2 },
                new() { Id = "eq-1", Type = LayoutElementType.Dock, Name = "EQ901", Aliases = ["EQ901"],
                        X = 18, Y = 11.5, W = 2, H = 2, Rotation = 180, Height = 1.5 },
                new() { Id = "cv-1", Type = LayoutElementType.Conveyor, From = "st-1", To = "st-2" },
                new() { Id = "ar-1", Type = LayoutElementType.Area, Name = "入庫區", X = 2, Y = 10.5, W = 14.5, H = 4, Rotation = 0 },
                new() { Id = "tx-1", Type = LayoutElementType.Label, Name = "北側", X = 2, Y = 0.5, W = 4, H = 1, Rotation = 270 },
            ],
        },
    };

    string PathOf(string name) => Path.Combine(_tmp.Path, name);

    [Fact]
    public void 存檔再讀回_內容完全相同()
    {
        var doc = Sample();
        var path = PathOf("範例廠.json");

        PlantConfigFile.Save(doc, path);
        var loaded = PlantConfigFile.Load(path);

        Assert.Equal(PlantConfigFile.Serialize(doc), PlantConfigFile.Serialize(loaded));
        Assert.Equal(File.ReadAllText(path), PlantConfigFile.Serialize(loaded));
        Assert.Equal([new("樓層", "1F"), new("備註", "虛構")], loaded.Layout.Elements[0].Props!); // 自訂屬性保持順序
        Assert.Equal(LayoutElementType.Conveyor, loaded.Layout.Elements[5].Type);
        Assert.Equal(["EXE", "PLC", "Server"], loaded.ModuleOrder);
    }

    [Fact]
    public void 檔案格式_欄位名稱與種類採小寫英文_用不到的欄位不寫出()
    {
        var json = PlantConfigFile.Serialize(Sample());

        Assert.Contains("\"formatVersion\": 1", json);
        Assert.Contains("\"type\": \"station\"", json);
        Assert.Contains("\"樓層\": \"1F\"", json);
        Assert.DoesNotContain("\"from\": null", json);
        Assert.DoesNotContain("\\u", json); // 中文不跳脫
    }

    [Fact]
    public void 每次存檔修訂號加1_記錄修改人與時間()
    {
        var doc = Sample();
        var path = PathOf("修訂.json");

        PlantConfigFile.Save(doc, path);
        PlantConfigFile.Save(doc, path);

        var loaded = PlantConfigFile.Load(path);
        Assert.Equal(2, loaded.Revision);
        Assert.Equal($@"{Environment.UserDomainName}\{Environment.UserName}", loaded.RevisedBy);
        Assert.InRange(loaded.RevisedAt!.Value, DateTimeOffset.Now.AddMinutes(-1), DateTimeOffset.Now.AddMinutes(1));
    }

    [Fact]
    public void 讀到較新的格式版本時_明確提示()
    {
        var path = PathOf("新版.json");
        File.WriteAllText(path, """{ "formatVersion": 3, "plant": "虛構", "revision": 1, "layout": { "elements": [] } }""");

        var ex = Assert.Throws<PlantConfigFormatException>(() => PlantConfigFile.Load(path));

        Assert.Contains("格式版本是 3", ex.Message);
        Assert.Contains("較新版的 LogTrace", ex.Message);
    }

    [Theory]
    [InlineData("""{ "plant": "虛構" }""", "缺少格式版本")]
    [InlineData("""{ "formatVersion": 0, "plant": "虛構" }""", "不認得的格式版本")]
    [InlineData("""{ "formatVersion": "1" }""", "缺少格式版本")]
    [InlineData("不是 JSON", "不是有效的 JSON")]
    [InlineData("""{ "formatVersion": 1, "plant": "虛構", "layout": { "elements": [ { "id": "x", "type": "spaceship" } ] } }""", "內容有誤")]
    public void 讀到未知的格式或內容時_明確提示(string json, string expected)
    {
        var ex = Assert.Throws<PlantConfigFormatException>(() => PlantConfigFile.Deserialize(json));

        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void 認不得的欄位原樣保留_存檔後不遺失()
    {
        var json = """
            {
              "formatVersion": 1,
              "plant": "虛構",
              "timeZone": "Asia/Taipei",
              "layout": { "units": "m", "elements": [ { "id": "st-1", "type": "station", "name": "站", "aliases": ["A-01"], "color": "#FF0000" } ], "background": "x.png" }
            }
            """;
        var path = PathOf("未知欄位.json");

        PlantConfigFile.Save(PlantConfigFile.Deserialize(json), path);
        var text = File.ReadAllText(path);

        Assert.Contains("\"timeZone\": \"Asia/Taipei\"", text);
        Assert.Contains("\"color\": \"#FF0000\"", text);
        Assert.Contains("\"background\": \"x.png\"", text);
    }

    [Fact]
    public void 別名重複時無法存檔_指出衝突的元素_檔案與修訂號不變()
    {
        var doc = Sample();
        var path = PathOf("重複.json");
        PlantConfigFile.Save(doc, path);
        var before = File.ReadAllText(path);
        doc.Layout.Elements[3].Aliases!.Add("a-01"); // 大小寫視為相同

        var ex = Assert.Throws<PlantConfigValidationException>(() => PlantConfigFile.Save(doc, path));

        var issue = Assert.Single(ex.Issues);
        Assert.Equal(["st-1", "vh-1"], issue.ElementIds);
        Assert.Contains("「入庫口1」", issue.Message);
        Assert.Contains("「SRCA01」", issue.Message);
        Assert.Equal(before, File.ReadAllText(path));
        Assert.Equal(1, doc.Revision);
    }

    [Fact]
    public void 存檔前的其他檢查_旋轉角度_輸送線兩端_輔助元素不能有別名_屬性重複()
    {
        var doc = Sample();
        doc.Layout.Elements[1].Rotation = 45;
        doc.Layout.Elements[5].To = "rk-1";
        doc.Layout.Elements[6].Aliases = ["Z-01"];
        doc.Layout.Elements[0].Props!.Add(new("樓層", "2F"));

        var issues = PlantConfigFile.Validate(doc);

        Assert.Equal(4, issues.Count);
        Assert.Contains(issues, i => i.Message.Contains("45 度") && i.ElementIds.SequenceEqual(["st-2"]));
        Assert.Contains(issues, i => i.Message.Contains("輸送線") && i.ElementIds.SequenceEqual(["cv-1"]));
        Assert.Contains(issues, i => i.Message.Contains("不能有別名") && i.ElementIds.SequenceEqual(["ar-1"]));
        Assert.Contains(issues, i => i.Message.Contains("「樓層」重複"));
    }

    [Fact]
    public void 設定檔存放位置_列出與建議檔名()
    {
        var library = new PlantConfigLibrary(PathOf("Plants"));
        Assert.Empty(library.List());

        var first = library.SuggestPath("範例廠");
        PlantConfigFile.Save(Sample(), first);
        var second = library.SuggestPath("範例廠");

        Assert.EndsWith("範例廠.json", first);
        Assert.EndsWith("範例廠 (2).json", second);
        Assert.Equal([first], library.List());
    }
}
