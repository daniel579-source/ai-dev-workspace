using LogTrace.Engine.Plants;

namespace LogTrace.Engine.Tests;

// 同一樓層的上層與下層（層段）：元素的 deck、樓層的 upperHeight。內容全部是虛構的。
public sealed class FloorDeckTests
{
    const string OldFile = """
        { "formatVersion": 9, "plant": "範例廠", "revision": 1,
          "layout": { "floors": ["1F", "2F"], "elements": [
            { "id": "st-1", "type": "station", "name": "範例站點 1", "floor": "1F", "x": 0, "y": 0, "w": 1, "h": 1 },
            { "id": "st-2", "type": "station", "name": "範例站點 2", "floor": "1F", "x": 4, "y": 0, "w": 1, "h": 1 },
            { "id": "cv-1", "type": "conveyor", "from": "st-1", "to": "st-2" } ] } }
        """;

    static LayoutElement Find(LayoutSection layout, string id) => layout.Elements.Single(e => e.Id == id);

    [Fact]
    public void 舊設定檔沒有層段_全部是下層_存檔不寫出deck也不升版()
    {
        var doc = PlantConfigFile.Deserialize(OldFile);

        Assert.All(doc.Layout.Elements, e => Assert.Equal(LayoutDeck.Lower, LayoutFloors.DeckOf(e, id => Find(doc.Layout, id!))));
        var json = PlantConfigFile.Serialize(doc);
        Assert.DoesNotContain("deck", json);
        Assert.DoesNotContain("\"formatVersion\": 10", json);
    }

    [Fact]
    public void 上層元素與上層高度_寫成deck與upperHeight_格式版本10_讀回來相同()
    {
        var doc = PlantConfigFile.Deserialize(OldFile);
        Find(doc.Layout, "st-1").Deck = LayoutDeck.Upper;
        doc.Layout.Floors = [new LayoutFloor("1F", UpperHeight: 2.2), "2F"];

        var json = PlantConfigFile.Serialize(doc);

        Assert.Contains("\"formatVersion\": 10", json);
        Assert.Contains("\"deck\": \"upper\"", json);
        Assert.Contains("\"upperHeight\": 2.2", json);
        var back = PlantConfigFile.Deserialize(json).Layout;
        Assert.Equal(LayoutDeck.Upper, Find(back, "st-1").Deck);
        Assert.Null(Find(back, "st-2").Deck);
        Assert.Equal(2.2, LayoutFloors.UpperHeightOf(back, "1F"));
    }

    [Fact]
    public void 沒有填上層高度時_使用預設()
    {
        var layout = PlantConfigFile.Deserialize(OldFile).Layout;
        layout.Floors = [new LayoutFloor("1F", 6), "2F"];

        Assert.Equal(LayoutFloors.DefaultUpperHeight(6), LayoutFloors.UpperHeightOf(layout, "1F"));
        Assert.Equal(LayoutFloors.DefaultUpperHeight(LayoutFloors.DefaultHeight), LayoutFloors.UpperHeightOf(layout, "2F"));
        Assert.Equal(0, LayoutFloors.DeckBase(layout, "1F", LayoutDeck.Lower));
    }

    [Fact]
    public void 輸送線取起點的層段_電梯一律為下層()
    {
        var layout = PlantConfigFile.Deserialize(OldFile).Layout;
        Find(layout, "st-1").Deck = LayoutDeck.Upper;
        var elevator = new LayoutElement { Id = "el-1", Type = LayoutElementType.Elevator, Deck = LayoutDeck.Upper };

        Assert.Equal(LayoutDeck.Upper, LayoutFloors.DeckOf(Find(layout, "cv-1"), id => Find(layout, id!)));
        Assert.Equal(LayoutDeck.Lower, LayoutFloors.DeckOf(elevator, _ => null));
    }

    [Fact]
    public void 存檔前補齊_下層與輸送線電梯不寫出deck()
    {
        var doc = PlantConfigFile.Deserialize(OldFile);
        Find(doc.Layout, "st-2").Deck = LayoutDeck.Lower;
        Find(doc.Layout, "cv-1").Deck = LayoutDeck.Upper;

        LayoutFloors.Normalize(doc.Layout);

        Assert.All(doc.Layout.Elements, e => Assert.Null(e.Deck));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(7)]
    public void 存檔前檢查_上層高度要大於0且小於樓板高度(double upper)
    {
        var doc = PlantConfigFile.Deserialize(OldFile);
        doc.Layout.Floors = [new LayoutFloor("1F", UpperHeight: upper), "2F"];

        Assert.Contains(PlantConfigFile.Validate(doc), i => i.Message.Contains("上層高度"));
    }

    [Fact]
    public void 存檔前檢查_搬送機和它的導軌要在同一個層段()
    {
        var doc = PlantConfigFile.Deserialize(OldFile);
        doc.Layout.Elements.Add(new LayoutElement
        {
            Id = "rail-1", Type = LayoutElementType.Rail, Name = "範例導軌", Floor = "1F",
            Points = [new RailPoint(0, 5), new RailPoint(10, 5)],
        });
        doc.Layout.Elements.Add(new LayoutElement
        {
            Id = "vh-1", Type = LayoutElementType.Vehicle, Name = "範例搬送機", Floor = "1F", Deck = LayoutDeck.Upper,
            X = 0, Y = 4.5, W = 1, H = 1, Rail = "rail-1",
        });

        Assert.Contains(PlantConfigFile.Validate(doc), i => i.Message.Contains("同一個層段"));
        Find(doc.Layout, "rail-1").Deck = LayoutDeck.Upper;
        Assert.DoesNotContain(PlantConfigFile.Validate(doc), i => i.Message.Contains("同一個層段"));
    }
}
