using LogTrace.Engine.Plants;

namespace LogTrace.Engine.Tests;

// 樓板高度（ADR-0017 第 6 點，#113）：3D 回放把樓層上下堆疊時使用。內容全部是虛構的。
public sealed class FloorHeightTests
{
    const string OldFile = """
        { "formatVersion": 5, "plant": "範例廠", "revision": 1,
          "layout": { "floors": ["1F", "2F", "3F"], "elements": [] } }
        """;

    [Fact]
    public void 舊設定檔的樓層是名稱_樓板高度預設5公尺_沒有填高度時照舊寫成名稱且不升版()
    {
        var doc = PlantConfigFile.Deserialize(OldFile);

        Assert.Equal(["1F", "2F", "3F"], LayoutFloors.Of(doc.Layout));
        Assert.Equal(5, LayoutFloors.HeightOf(doc.Layout, "2F"));
        Assert.Equal([("1F", 0.0, 5.0), ("2F", 5.0, 5.0), ("3F", 10.0, 5.0)], LayoutFloors.Stack(doc.Layout));

        var json = PlantConfigFile.Serialize(doc);
        Assert.Contains("\"formatVersion\": 1", json); // 沒有用到新功能：不升版（ADR-0006 第 7 點）
        Assert.Contains("\"1F\",", json);
    }

    [Fact]
    public void 填了樓板高度_樓層寫成物件陣列_格式版本6_讀回來相同()
    {
        var doc = PlantConfigFile.Deserialize(OldFile);
        doc.Layout.Floors = [new LayoutFloor("1F", 6.5), "2F", new LayoutFloor("3F", 4)];

        var json = PlantConfigFile.Serialize(doc);

        Assert.Contains("\"formatVersion\": 6", json);
        Assert.Contains("\"name\": \"1F\"", json);
        Assert.Contains("\"height\": 6.5", json);
        var back = PlantConfigFile.Deserialize(json).Layout;
        Assert.Equal([new LayoutFloor("1F", 6.5), new LayoutFloor("2F"), new LayoutFloor("3F", 4)], back.Floors);
        Assert.Equal([("1F", 0.0, 6.5), ("2F", 6.5, 5.0), ("3F", 11.5, 4.0)], LayoutFloors.Stack(back));
    }

    [Fact]
    public void 版本6的設定檔可以讀_名稱與物件混用也可以讀()
    {
        var doc = PlantConfigFile.Deserialize("""
            { "formatVersion": 6, "plant": "範例廠", "revision": 1,
              "layout": { "floors": [ { "name": "1F", "height": 7 }, "2F" ], "elements": [] } }
            """);

        Assert.Equal([new LayoutFloor("1F", 7), new LayoutFloor("2F")], doc.Layout.Floors);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(150)]
    public void 存檔前檢查_樓板高度要大於0且不超過100公尺(double height)
    {
        var doc = PlantConfigFile.Deserialize(OldFile);
        doc.Layout.Floors = [new LayoutFloor("1F", height), "2F"];

        Assert.Contains(PlantConfigFile.Validate(doc), i => i.Message.Contains("樓板高度"));
    }

    [Fact]
    public void 樓層缺少名稱或高度不是數字_明確回報讀不了()
    {
        Assert.Throws<PlantConfigFormatException>(() => PlantConfigFile.Deserialize("""
            { "formatVersion": 6, "plant": "範例廠", "revision": 1, "layout": { "floors": [ { "height": 7 } ], "elements": [] } }
            """));
        Assert.Throws<PlantConfigFormatException>(() => PlantConfigFile.Deserialize("""
            { "formatVersion": 6, "plant": "範例廠", "revision": 1, "layout": { "floors": [ { "name": "1F", "height": "高" } ], "elements": [] } }
            """));
    }
}
