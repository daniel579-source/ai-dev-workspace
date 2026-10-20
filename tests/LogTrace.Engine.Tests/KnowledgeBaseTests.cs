using System.Text;
using LogTrace.Engine.Knowledge;
using LogTrace.Engine.Plants;
using LogTrace.Tests.Support;

namespace LogTrace.Engine.Tests;

// 知識庫第一版（#80，ADR-0013 第 2 點）：每個廠別一個 <廠別設定檔名>.kb.csv。代碼與說明全部是虛構的。
public sealed class KnowledgeBaseTests : IDisposable
{
    readonly TempFolder _tmp = new();

    public void Dispose() => _tmp.Dispose();

    static KnowledgeBase Parse(string csv) => KnowledgeBase.Parse(Encoding.UTF8.GetBytes(csv));

    [Fact]
    public void 讀取四個欄位_處置與出處可以空白()
    {
        var kb = Parse("代碼,說明,處置,出處\nE101,虛構說明甲,虛構處置甲,虛構手冊 p.3\nE102,虛構說明乙,,\n");

        Assert.Empty(kb.Issues);
        Assert.Equal(
            [new KnowledgeEntry(2, "E101", "虛構說明甲", "虛構處置甲", "虛構手冊 p.3"), new KnowledgeEntry(3, "E102", "虛構說明乙", "", "")],
            kb.Entries);
    }

    [Fact]
    public void 標題的欄位順序可以不同_多出的欄位不使用()
    {
        var kb = Parse("出處,代碼,備註,說明,處置\n虛構手冊,E101,略,虛構說明,虛構處置\n");

        Assert.Equal(new KnowledgeEntry(2, "E101", "虛構說明", "虛構處置", "虛構手冊"), Assert.Single(kb.Entries));
    }

    [Fact]
    public void 雙引號內可以有逗號_雙引號與換行_行號為該列開始的行()
    {
        var kb = Parse("代碼,說明,處置,出處\n\"E101\",\"說明, 含逗號\",\"第一步\n第二步\",\"手冊 \"\"甲\"\"\"\nE102,虛構說明,,\n");

        Assert.Empty(kb.Issues);
        Assert.Equal(new KnowledgeEntry(2, "E101", "說明, 含逗號", "第一步\n第二步", "手冊 \"甲\""), kb.Entries[0]);
        Assert.Equal(4, kb.Entries[1].Line);
    }

    [Fact]
    public void UTF8有BOM_與Big5都可以讀取()
    {
        var csv = "代碼,說明,處置,出處\r\nE101,虛構說明,虛構處置,虛構手冊\r\n";
        var withBom = KnowledgeBase.Parse([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv)]);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var big5 = KnowledgeBase.Parse(Encoding.GetEncoding(950).GetBytes(csv));

        var expected = new KnowledgeEntry(2, "E101", "虛構說明", "虛構處置", "虛構手冊");
        Assert.Equal(expected, Assert.Single(withBom.Entries));
        Assert.Equal(expected, Assert.Single(big5.Entries));
    }

    [Fact]
    public void 代碼或說明空白的列不使用_列出行號與原因_空白列略過()
    {
        var kb = Parse("代碼,說明,處置,出處\n,虛構說明,,\nE102,,,\n\nE103,虛構說明,,\n");

        Assert.Equal(["E103"], kb.Entries.Select(e => e.Code));
        Assert.Equal([new KnowledgeIssue(2, "沒有填代碼"), new KnowledgeIssue(3, "沒有填說明")], kb.Issues);
    }

    [Fact]
    public void 標題缺少代碼或說明欄時_整個檔案不使用()
    {
        var kb = Parse("編號,說明\nE101,虛構說明\n");

        Assert.Empty(kb.Entries);
        Assert.Equal(new KnowledgeIssue(1, "標題列缺少「代碼」欄"), Assert.Single(kb.Issues));
    }

    [Fact]
    public void 雙引號沒有結束時_列出行號()
    {
        var kb = Parse("代碼,說明,處置,出處\nE101,\"未結束的說明\n");

        Assert.Empty(kb.Entries);
        Assert.Equal(new KnowledgeIssue(2, "雙引號沒有結束"), Assert.Single(kb.Issues));
    }

    [Fact]
    public void 代碼完全比對_不分大小寫_全形與半形相同_同一代碼多列依檔案順序()
    {
        var kb = Parse("代碼,說明,處置,出處\nE101,虛構說明甲,,\nE1011,虛構說明乙,,\ne101,虛構說明丙,,\n");

        Assert.Equal(["虛構說明甲", "虛構說明丙"], kb.Lookup("Ｅ１０１").Select(e => e.Description));
        Assert.Equal(["虛構說明甲", "虛構說明丙"], kb.Lookup(" e101 ").Select(e => e.Description));
        Assert.Empty(kb.Lookup("E10"));
    }

    [Fact]
    public void 關鍵字搜尋四個欄位_部分比對_多個關鍵字都要包含()
    {
        var kb = Parse("代碼,說明,處置,出處\nE101,輸送帶馬達過載,檢查馬達,虛構手冊A\nE102,站點光電異常,清潔光電,虛構手冊B\nW201,馬達溫度偏高,,虛構手冊A\n");

        Assert.Equal(["E101", "W201"], kb.Search("馬達").Select(e => e.Code));
        Assert.Equal(["E101"], kb.Search("馬達 手冊ａ 過載").Select(e => e.Code));
        Assert.Equal(["E101", "E102"], kb.Search("e10").Select(e => e.Code));
        Assert.Equal(3, kb.Search("  ").Count);
    }

    [Fact]
    public void 依案件的廠別名稱找設定檔_知識庫檔在設定檔旁邊()
    {
        var plants = Path.Combine(_tmp.Path, "Plants");
        var lib = new PlantConfigLibrary(plants);
        PlantConfigFile.Save(new PlantConfigDocument { Plant = "甲廠" }, Path.Combine(plants, "a.json"));
        PlantConfigFile.Save(new PlantConfigDocument { Plant = "乙廠" }, Path.Combine(plants, "b.json"));
        File.WriteAllText(Path.Combine(plants, "b.kb.csv"), "代碼,說明,處置,出處\nE101,虛構說明,,\n");

        Assert.Equal(Path.Combine(plants, "b.kb.csv"), KnowledgeBase.PathFor("乙廠", null, lib));
        Assert.Equal(Path.Combine(plants, "a.kb.csv"), KnowledgeBase.PathFor("甲廠", null, lib));
        Assert.Null(KnowledgeBase.PathFor("丙廠", null, lib));

        Assert.Single(KnowledgeBase.Load(Path.Combine(plants, "b.kb.csv"))!.Entries);
        Assert.Null(KnowledgeBase.Load(Path.Combine(plants, "a.kb.csv"))); // 檔案不存在
    }

    [Fact]
    public void 快照的來源路徑優先()
    {
        var elsewhere = Path.Combine(_tmp.Path, "其他位置");
        var lib = new PlantConfigLibrary(Path.Combine(_tmp.Path, "Plants"));
        PlantConfigFile.Save(new PlantConfigDocument { Plant = "甲廠" }, Path.Combine(lib.Root, "a.json"));
        PlantConfigFile.Save(new PlantConfigDocument { Plant = "甲廠" }, Path.Combine(elsewhere, "x.json"));

        Assert.Equal(Path.Combine(elsewhere, "x.kb.csv"), KnowledgeBase.PathFor("甲廠", Path.Combine(elsewhere, "x.json"), lib));
    }
}
