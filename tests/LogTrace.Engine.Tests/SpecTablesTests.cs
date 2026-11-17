using System.Text;
using LogTrace.Engine.Knowledge;
using LogTrace.Tests.Support;

namespace LogTrace.Engine.Tests;

// 電文／代碼對照表（ADR-0021）。內容全部是虛構的，不是任何真實規格書。
public sealed class SpecTablesTests
{
    const string Header = "設備種類,類別,代碼或名稱,意義（原文）,意義（中文）,出處,規格書版本,狀態,審核者,審核日期";

    static (string, byte[], string?) File(string name, string? folderDevice, params string[] lines) =>
        (name, Encoding.UTF8.GetBytes(string.Join("\r\n", lines)), folderDevice);

    [Fact]
    public void 共用與各廠補充_讀取後依代碼查詢_依設備種類過濾_翻譯與待確認可以分辨()
    {
        var t = SpecTables.Parse(
        [
            File(@"shared\RM\rm.csv", "RM", Header,
                ",錯誤碼,E01,荷役異常（虛構）,貨叉動作異常（虛構）,虛構 RM 通信仕樣書 第 5 章 p.42,v1.0,已審核,工程師甲,2030-01-10",
                "RM,狀態碼,S10,運転中（虛構）,運轉中（虛構）,虛構 RM 通信仕樣書 第 4 章 p.30,v1.0,待確認,,"),
            File(@"shared\STV\stv.csv", "STV", Header,
                "STV,錯誤碼,E01,走行異常（虛構）,,虛構 STV 通信仕樣書 3.2 節 p.18,v2.1,已審核,工程師乙,2030-01-11"),
            File(@"plants\範例廠\plant.csv", null, Header,
                "STV,錯誤碼,E99,本廠自訂（虛構）,,虛構廠內說明 p.1,2030-01,已審核,工程師丙,2030-01-12"),
        ]);

        Assert.Empty(t.Issues);
        Assert.Equal(4, t.Entries.Count);
        Assert.Equal(["RM", "STV"], t.Lookup("e01").Select(e => e.Device)); // 不分大小寫；沒有指定設備時兩種都列出
        var rm = Assert.Single(t.Lookup("Ｅ０１", ["RM"])); // 全形半形相同；只查 RM
        Assert.Equal(("RM", "貨叉動作異常（虛構）", true, true), (rm.Device, rm.Chinese, rm.IsTranslated, rm.IsReviewed));
        Assert.False(t.Lookup("S10").Single().IsReviewed);
        Assert.Equal("E99", Assert.Single(t.Search("本廠")).Code);
        Assert.Equal("對照表 3 個檔、4 筆（已審核 3、待確認 1；翻譯 2）", t.Summary);
    }

    [Fact]
    public void 格式有誤的列不使用_重複不使用_同一代碼意義不同兩列都留下並提醒_沒有版本提醒()
    {
        var t = SpecTables.Parse(
        [
            File(@"shared\AGC\agc.csv", "AGC", Header,
                "AGC,錯誤碼,,意義（虛構）,,p.1,v1,已審核,,",             // 第 2 行：沒有代碼
                "AGC,錯誤碼,A1,,,p.1,v1,已審核,,",                         // 第 3 行：沒有原文
                "AGC,錯誤碼,A2,意義（虛構）,,,v1,已審核,,",                 // 第 4 行：沒有出處
                "RM,錯誤碼,A3,意義（虛構）,,p.1,v1,已審核,,",               // 第 5 行：設備和資料夾不同
                "AGC,錯誤碼,A4,意義（虛構）,,p.1,v1,完成,,",                // 第 6 行：狀態不對
                "AGC,錯誤碼,A5,停止（虛構）,,p.2,v1,已審核,,",
                "AGC,錯誤碼,A5,停止（虛構）,,p.2,v1,已審核,,",              // 第 8 行：重複
                "AGC,錯誤碼,A5,暫停（虛構）,,p.3,v1,待確認,,",              // 第 9 行：同一代碼意義不同
                "AGC,錯誤碼,A6,意義（虛構）,,p.4,,已審核,,"),               // 第 10 行：沒有版本
            File(@"plants\範例廠\x.csv", null, "類別,代碼或名稱,意義（原文）,出處,狀態", "錯誤碼,B1,意義,p.1,已審核"), // 缺設備種類欄
        ]);

        var errors = t.Issues.Where(i => !i.Warning).Select(i => (i.Line, i.Reason)).ToList();
        Assert.Contains((2, "沒有填代碼或名稱"), errors);
        Assert.Contains((3, "沒有填意義（原文）"), errors);
        Assert.Contains((4, "沒有填出處"), errors);
        Assert.Contains(errors, e => e.Line == 5 && e.Reason.Contains("和資料夾「AGC」不同"));
        Assert.Contains(errors, e => e.Line == 6 && e.Reason.Contains("狀態「完成」"));
        Assert.Contains(errors, e => e.Line == 8 && e.Reason.Contains("重複"));
        Assert.Contains(t.Issues, i => i.File.EndsWith("x.csv") && i.Reason.Contains("標題列缺少「設備種類」欄"));
        var warnings = t.Issues.Where(i => i.Warning).Select(i => i.Line).ToList();
        Assert.Equal([9, 10], warnings);
        Assert.Equal(2, t.Lookup("A5").Count); // 意義不同的兩列都顯示
        Assert.Contains("7 列有誤，沒有使用；2 處需要注意", t.Summary); // 6 列＋缺標題欄的檔案
    }

    [Fact]
    public void 從知識庫資料夾讀取_半形括號標題_Big5_資料夾不存在時為空()
    {
        using var tmp = new TempFolder();
        Assert.Equal("沒有對照表", SpecTables.Load(tmp.Path, "範例廠").Summary);

        var rm = Directory.CreateDirectory(Path.Combine(tmp.Path, "shared", "RM")).FullName;
        System.IO.File.WriteAllText(Path.Combine(rm, "a.csv"), "代碼或名稱,意義(原文),出處,狀態\r\nE01,異常（虛構）,p.1,已審核", Encoding.UTF8);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var plant = Directory.CreateDirectory(Path.Combine(tmp.Path, "plants", "範例廠")).FullName;
        System.IO.File.WriteAllBytes(Path.Combine(plant, "b.csv"),
            Encoding.GetEncoding(950).GetBytes("設備種類,代碼或名稱,意義（原文）,出處,狀態\r\nSTV,E02,走行停止（虛構）,p.2,待確認"));
        Directory.CreateDirectory(Path.Combine(tmp.Path, "plants", "別廠"));

        var t = SpecTables.Load(tmp.Path, "範例廠");

        Assert.Equal([@"shared\RM\a.csv", @"plants\範例廠\b.csv"], t.Files);
        Assert.Equal(("RM", "異常（虛構）"), (t.Lookup("E01").Single().Device, t.Lookup("E01").Single().Original));
        Assert.Equal("走行停止（虛構）", t.Lookup("E02").Single().Original);
        Assert.Contains("沒有填規格書版本", t.Issues.First().Reason);
    }

    [Fact]
    public void 指令包的虛構範例輸出_通過檢查_沒有有誤的列()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(Path.Combine(dir.FullName, "LogTrace.slnx"))) dir = dir.Parent;
        var examples = Path.Combine(dir!.FullName, "tools", "spec-extract", "examples");

        var t = SpecTables.Parse(
        [
            ("expected-rm.csv", System.IO.File.ReadAllBytes(Path.Combine(examples, "expected-rm.csv")), "RM"),
            ("expected-stv.csv", System.IO.File.ReadAllBytes(Path.Combine(examples, "expected-stv.csv")), "STV"),
        ]);

        Assert.Empty(t.Issues);
        Assert.Equal(12, t.Entries.Count);
        Assert.Equal("貨叉動作異常／處置：確認貨叉位置感測器", t.Lookup("E01", ["RM"]).Single().Chinese);
        Assert.All(t.Entries, e => Assert.Equal(SpecTables.Pending, e.Status)); // Copilot 輸出一律待確認
    }
}
