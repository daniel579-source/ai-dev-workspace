using System.Text;
using LogTrace.Engine.Cases;
using LogTrace.Engine.Flows;
using LogTrace.Engine.Parsing;
using LogTrace.Engine.Plants;
using LogTrace.Tests.Support;
using Microsoft.Data.Sqlite;

namespace LogTrace.Engine.Tests;

// 站點與 Conv 合併為 Conv、EQ 負責交出（ADR-0026）：舊設定檔、舊的群組名稱、舊案件資料庫、舊的段落種類名稱。內容全部是虛構的。
public sealed class ConvEqTests : IDisposable
{
    readonly TempFolder _tmp = new();

    public void Dispose() => _tmp.Dispose();

    const string OldFile = """
        { "formatVersion": 9, "plant": "範例廠", "revision": 1,
          "layout": { "elements": [
            { "id": "st-1", "type": "station", "name": "範例站點", "aliases": ["A-01"], "x": 0, "y": 0, "w": 2, "h": 2 },
            { "id": "eq-1", "type": "dock", "name": "範例 Conv", "aliases": ["P1"], "x": 4, "y": 0, "w": 2, "h": 2 },
            { "id": "eqp-1", "type": "eq", "name": "範例 EQ", "x": 8, "y": 0, "w": 1.5, "h": 1.5 } ] } }
        """;

    [Fact]
    public void 舊設定檔的station與dock都讀成Conv_別名保留_存檔寫conv_格式版本11()
    {
        var doc = PlantConfigFile.Deserialize(OldFile);

        Assert.Equal([LayoutElementType.Conv, LayoutElementType.Conv, LayoutElementType.Eq], doc.Layout.Elements.Select(e => e.Type));
        Assert.Equal(["A-01"], doc.Layout.Elements[0].Aliases);
        Assert.Equal(["P1"], doc.Layout.Elements[1].Aliases);
        var json = PlantConfigFile.Serialize(doc);
        Assert.Contains("\"formatVersion\": 11", json);
        Assert.Contains("\"type\": \"conv\"", json);
        Assert.DoesNotContain("\"station\"", json);
        Assert.DoesNotContain("\"dock\"", json);
        Assert.Equal(LayoutElementType.Conv, PlantConfigFile.Deserialize(json).Layout.Elements[1].Type);
    }

    [Fact]
    public void 沒有Conv時_照舊寫最低版本()
    {
        var doc = PlantConfigFile.Deserialize(OldFile);
        doc.Layout.Elements.RemoveAll(e => e.Type == LayoutElementType.Conv);

        Assert.Equal(9, doc.RequiredFormatVersion()); // 只有 EQ
    }

    [Fact]
    public void EQ可以有別名_可以當輸送線的端點()
    {
        Assert.True(LayoutElement.CanHaveAliases(LayoutElementType.Eq));
        Assert.True(LayoutElement.CanBeConveyorEnd(LayoutElementType.Eq));
        Assert.True(LayoutElement.IsStationLike(LayoutElementType.Conv));
        Assert.False(LayoutElement.IsStationLike(LayoutElementType.Eq));
    }

    [Fact]
    public void 規則中舊的群組名稱station與dock_抽取時視為conv與eq_檢查也認得()
    {
        var rule = new ParseRule("舊名稱", null, null, @"讀到 (?<station>A-[0-9]{2}) 棧板=(?<pallet>[0-9]+) 交給 (?<dock>EQ[0-9]{3})", EffectKinds.PalletRead);

        Assert.Null(FieldExtractor.Check(rule));
        var (fields, effects) = new FieldExtractor([rule]).ExtractWithEffects(1, "PLC", "PLCLOG", "讀到 A-01 棧板=123 交給 EQ901");
        Assert.Equal([(FieldKinds.Conv, "A-01"), (FieldKinds.Pallet, "123"), (FieldKinds.Eq, "EQ901")], fields.Select(f => (f.Kind, f.Value)));
        Assert.Equal("A-01", Assert.Single(effects).Get(FieldKinds.Conv));
        Assert.Equal("Conv", FieldKinds.Label("station"));
        Assert.Equal("EQ", FieldKinds.Label(FieldKinds.Eq));
    }

    [Fact]
    public void 舊案件資料庫的欄位種類_讀取時對應到新名稱_搜尋與統計也查得到()
    {
        string folder;
        using (var c = new CaseLibrary(Path.Combine(_tmp.Path, "Cases")).Create("舊案件", PlantConfig.Default))
        {
            c.Import(TempFolder.Fixture("fields-cases"));
            folder = c.Folder;
        }
        // 模擬舊版程式建立的案件：欄位種類與效果參數是舊名稱
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(folder, Case.DatabaseFileName), Pooling = false }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE event_fields SET kind = 'station' WHERE kind = 'conv';
                UPDATE event_effects SET params = replace(params, '"conv"', '"station"');
                """;
            cmd.ExecuteNonQuery();
        }

        using var reopened = Case.Open(folder);
        var seqs = reopened.FindSequences([FieldKinds.Conv], ["A-01"]);
        Assert.NotEmpty(seqs);
        Assert.Contains(reopened.GetFields(seqs[0]), f => f.Kind == FieldKinds.Conv && f.Value == "A-01");
        Assert.Contains(reopened.GetIdentifierCounts(), x => x.Kind == FieldKinds.Conv && x.Value == "A-01");
        Assert.DoesNotContain(reopened.GetEffects(), e => e.Params.ContainsKey("station"));
        Assert.Contains(reopened.GetEffects(), e => e.Get(FieldKinds.Conv) == "A-01");
    }

    [Fact]
    public void 正常流程樣板中舊的段落種類名稱_站點改為Conv()
    {
        var file = FlowStore.Parse(Encoding.UTF8.GetBytes("""
            { "formatVersion": 1, "plant": "範例廠", "samples": [],
              "kinds": [ { "kind": "STV 站點 → Conv", "ready": true }, { "kind": "RM Conv → 站點", "ready": false } ] }
            """));

        Assert.Equal(["STV Conv → Conv", "RM Conv → Conv"], file.Kinds.Select(k => k.Kind));
    }
}
