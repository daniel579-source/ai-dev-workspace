using LogTrace.Engine.Plants;
using LogTrace.Tests.Support;

namespace LogTrace.Engine.Tests;

// 輸送線可以雙向、可以連到搬送機（ADR-0006 第 1、7 點，#63）。內容全部是虛構的。
public sealed class ConveyorTests : IDisposable
{
    readonly TempFolder _tmp = new();

    public void Dispose() => _tmp.Dispose();

    static PlantConfigDocument Sample(params LayoutElement[] conveyors) => new()
    {
        Plant = "範例廠",
        Layout = new LayoutSection
        {
            Elements =
            [
                new() { Id = "st-1", Type = LayoutElementType.Station, Name = "站點 1", X = 1, Y = 1, W = 2, H = 2, Rotation = 0 },
                new() { Id = "vh-1", Type = LayoutElementType.Vehicle, Name = "搬送機 1", X = 10, Y = 1, W = 2.5, H = 1.2, Rotation = 0 },
                new() { Id = "eq-1", Type = LayoutElementType.Dock, Name = "對接設備 1", X = 1, Y = 8, W = 2, H = 2, Rotation = 0 },
                new() { Id = "rk-1", Type = LayoutElementType.Rack, Name = "料架 1", X = 6, Y = 8, W = 12, H = 1.5, Rotation = 0,
                        Row = "01", ColFrom = 1, ColTo = 10, Levels = 5, LevelHeight = 2.5 },
                .. conveyors,
            ],
        },
    };

    [Fact]
    public void 輸送線連到搬送機_有交接點時可以存檔_雙向與交接點讀回不變()
    {
        var doc = Sample(new LayoutElement
        {
            Id = "cv-1", Type = LayoutElementType.Conveyor, From = "eq-1", To = "vh-1", Bidirectional = true, ToPoint = new(10, 1.6),
        });
        var path = Path.Combine(_tmp.Path, "範例廠.json");

        PlantConfigFile.Save(doc, path);
        var cv = PlantConfigFile.Load(path).Layout.Elements.Single(e => e.Id == "cv-1");

        Assert.True(cv.Bidirectional);
        Assert.Equal(new LayoutPoint(10, 1.6), cv.ToPoint);
        Assert.Null(cv.FromPoint);
        var json = File.ReadAllText(path);
        Assert.Contains("\"formatVersion\": 2", json); // 用到雙向與交接點：格式版本 2
        Assert.Contains("\"bidirectional\": true", json);
        Assert.Contains("\"toPoint\"", json);
        Assert.DoesNotContain("\"fromPoint\"", json);
    }

    [Fact]
    public void 單向的輸送線不寫出bidirectional_舊檔沒有這個欄位時視為單向()
    {
        var doc = Sample(new LayoutElement { Id = "cv-1", Type = LayoutElementType.Conveyor, From = "st-1", To = "eq-1" });

        var json = PlantConfigFile.Serialize(doc);

        Assert.DoesNotContain("bidirectional", json);
        Assert.Contains("\"formatVersion\": 1", json); // 沒用到新功能：仍為 1，舊版程式可以開啟
        Assert.Null(PlantConfigFile.Deserialize(json).Layout.Elements.Single(e => e.Id == "cv-1").Bidirectional);
    }

    [Fact]
    public void 存檔前檢查_連到搬送機沒有交接點_或連到料架時擋下()
    {
        var doc = Sample(
            new LayoutElement { Id = "cv-1", Type = LayoutElementType.Conveyor, From = "vh-1", To = "st-1" },
            new LayoutElement { Id = "cv-2", Type = LayoutElementType.Conveyor, From = "st-1", To = "rk-1" },
            new LayoutElement { Id = "cv-3", Type = LayoutElementType.Conveyor, From = "vh-1", To = "eq-1", FromPoint = new(10, 2.2) });

        var issues = PlantConfigFile.Validate(doc);

        Assert.Equal(2, issues.Count);
        Assert.Contains(issues, i => i.Message.Contains("交接點") && i.ElementIds.SequenceEqual(["cv-1"]));
        Assert.Contains(issues, i => i.Message.Contains("站點、對接設備或搬送機") && i.ElementIds.SequenceEqual(["cv-2"]));
    }

    [Theory]
    [InlineData(0, 30, 1.6, 12.5, 1.6)]   // 在右邊：右邊緣
    [InlineData(0, 0, 0, 10, 1)]          // 在左上：左上角
    [InlineData(0, 10.2, 1.6, 10, 1.6)]   // 在裡面、靠左：投影到左邊緣
    [InlineData(90, 30, 1.6, 11.85, 1.6)] // 旋轉 90 度：寬深對調（中心 11.25、1.6，橫向半寬 0.6）
    public void 交接點的預設位置_搬送機圖示上最靠近另一端的邊緣點(int rotation, double tx, double ty, double x, double y)
    {
        var p = LayoutGeometry.NearestEdgePoint(10, 1, 2.5, 1.2, rotation, tx, ty);

        Assert.Equal(new LayoutPoint(x, y), p);
    }
}
