using LogTrace.Engine.Plants;

namespace LogTrace.Engine.Tests;

// 樓層（ADR-0006 第 13 點，#99）。內容全部是虛構的。
public sealed class FloorTests
{
    static LayoutElement Station(string id, string? floor, double x = 1) =>
        new() { Id = id, Type = LayoutElementType.Station, Name = id, Floor = floor, X = x, Y = 1, W = 2, H = 2, Rotation = 0 };

    [Fact]
    public void 舊設定檔沒有樓層_視為只有1F_存檔時一律寫出樓層_格式版本不變()
    {
        var doc = PlantConfigFile.Deserialize("""
            { "formatVersion": 1, "plant": "範例廠", "revision": 1,
              "layout": { "elements": [ { "id": "st-1", "type": "station", "x": 1, "y": 1, "w": 2, "h": 2, "rotation": 0 },
                                        { "id": "st-2", "type": "station", "x": 5, "y": 1, "w": 2, "h": 2, "rotation": 0 },
                                        { "id": "cv-1", "type": "conveyor", "from": "st-1", "to": "st-2" } ] } }
            """);

        Assert.Equal(["1F"], LayoutFloors.Of(doc.Layout));
        var json = PlantConfigFile.Serialize(doc);

        Assert.Contains("\"formatVersion\": 1", json); // 不升格式版本（使用者決定）
        Assert.Contains("\"floors\": [", json);
        var back = PlantConfigFile.Deserialize(json).Layout;
        Assert.Equal(["1F"], back.Floors);
        Assert.All(back.Elements.Where(e => e.Type == LayoutElementType.Station), e => Assert.Equal("1F", e.Floor));
        Assert.Null(back.Elements.Single(e => e.Id == "cv-1").Floor); // 輸送線不存樓層
    }

    [Fact]
    public void 輸送線畫在起點的樓層_大小寫不同的樓層名稱以清單中的寫法為準()
    {
        var floors = new List<string> { "1F", "2F" };
        var a = Station("st-1", "2f");
        var b = Station("st-2", "2F");
        var cv = new LayoutElement { Id = "cv-1", Type = LayoutElementType.Conveyor, From = "st-1", To = "st-2" };
        LayoutElement? Find(string? id) => new[] { a, b }.FirstOrDefault(e => e.Id == id);

        Assert.Equal("2F", LayoutFloors.FloorOf(a, floors, Find));
        Assert.Equal("2F", LayoutFloors.FloorOf(cv, floors, Find));
        Assert.Equal("1F", LayoutFloors.FloorOf(Station("st-3", null), floors, Find)); // 沒有樓層＝第一層
    }

    [Fact]
    public void 存檔前檢查_樓層名稱空白或重複_元素的樓層不在清單_輸送線兩端不同層()
    {
        var doc = new PlantConfigDocument
        {
            Plant = "範例廠",
            Layout = new LayoutSection
            {
                Floors = ["1F", "2F", "2f", " "],
                Elements =
                [
                    Station("st-1", "1F"), Station("st-2", "2F", 5), Station("st-3", "3F", 9),
                    new() { Id = "cv-1", Type = LayoutElementType.Conveyor, From = "st-1", To = "st-2" },
                ],
            },
        };

        var issues = PlantConfigFile.Validate(doc);

        Assert.Contains(issues, i => i.Message.Contains("空白的樓層名稱"));
        Assert.Contains(issues, i => i.Message.Contains("樓層「2F」重複"));
        Assert.Contains(issues, i => i.Message.Contains("「st-3」的樓層「3F」不在樓層清單中") && i.ElementIds.SequenceEqual(["st-3"]));
        Assert.Contains(issues, i => i.Message.Contains("兩端不在同一層") && i.ElementIds.Contains("cv-1"));
    }

    [Fact]
    public void 並排_橫向由左到右_垂直時低樓層在下_樓層之間留5公尺()
    {
        var sizes = new List<(string, double, double)> { ("1F", 60, 40), ("2F", 80, 50), ("3F", 60, 40) };

        var h = LayoutFloors.Arrange(sizes, vertical: false);
        Assert.Equal(["1F", "2F", "3F"], h.Select(r => r.Floor));
        Assert.Equal([0d, 65, 150], h.Select(r => r.OffsetX));
        Assert.All(h, r => Assert.Equal(LayoutFloors.LabelBand, r.OffsetY)); // 上方留名稱的位置

        var v = LayoutFloors.Arrange(sizes, vertical: true);
        Assert.Equal(["1F", "2F", "3F"], v.Select(r => r.Floor)); // 依樓層順序回傳
        Assert.True(v[0].OffsetY > v[1].OffsetY && v[1].OffsetY > v[2].OffsetY); // 1F 在最下面
        Assert.Equal(1.5, v[2].OffsetY);
        Assert.Equal(1.5 + 40 + 5 + 1.5, v[1].OffsetY);
        Assert.All(v, r => Assert.Equal(0, r.OffsetX));
    }

    [Fact]
    public void 點所在的樓層區域_落在空白處時取最近的()
    {
        var regions = LayoutFloors.Arrange([("1F", 60, 40), ("2F", 60, 40)], vertical: false);

        Assert.Equal("1F", LayoutFloors.RegionAt(regions, 10, 10)!.Floor);
        Assert.Equal("2F", LayoutFloors.RegionAt(regions, 70, 10)!.Floor);
        Assert.Equal("2F", LayoutFloors.RegionAt(regions, 64, 10)!.Floor); // 空白處：離 2F 比較近
        Assert.Equal("1F", LayoutFloors.RegionAt(regions, 10, 0.5)!.Floor); // 名稱那一條也算該層
    }
}
