using LogTrace.Engine.Plants;

namespace LogTrace.Engine.Tests;

// 站點模式代號的名稱（#58，ADR-0005 第 3 點）。代號與名稱全部是虛構的。
public sealed class StationModeNamesTests
{
    static PlantConfigDocument Doc(params (string Code, string Name)[] names) => new()
    {
        Plant = "範例廠",
        StationModeNames = names.Select(n => KeyValuePair.Create(n.Code, n.Name)).ToList(),
    };

    [Fact]
    public void 存成代號對名稱的物件_讀回後順序與內容相同()
    {
        var json = PlantConfigFile.Serialize(Doc(("9", "虛構模式乙"), ("1", "虛構模式甲")));

        Assert.Contains("\"stationModeNames\": {", json);
        Assert.Contains("\"9\": \"虛構模式乙\"", json);
        Assert.Equal([new("9", "虛構模式乙"), new("1", "虛構模式甲")], PlantConfigFile.Deserialize(json).StationModeNames);
    }

    [Fact]
    public void 舊版設定檔沒有這個欄位_照常開啟_對照為空()
    {
        var doc = PlantConfigFile.Deserialize("""{ "formatVersion": 1, "plant": "舊廠" }""");

        Assert.Empty(doc.StationModeNames);
        Assert.Empty(PlantConfigFile.Validate(doc));
    }

    [Fact]
    public void 代號重複或沒填名稱時_無法存檔()
    {
        var issues = PlantConfigFile.Validate(Doc(("1", "虛構模式甲"), ("1", "虛構模式乙"), ("2", " ")));

        Assert.Contains(issues, i => i.Message.Contains("代號「1」重複"));
        Assert.Contains(issues, i => i.Message.Contains("沒有填代號或名稱"));
    }
}
