using LogTrace.Engine.Cases;
using LogTrace.Engine.Events;
using LogTrace.Engine.Plants;
using LogTrace.Tests.Support;

namespace LogTrace.Engine.Tests;

// 案件管理：說明檔、可攜性、稽核紀錄、案件清單與刪除、OneDrive 警告（ADR-0009、ADR-0011）
public sealed class CaseManagementTests : IDisposable
{
    readonly TempFolder _tmp = new();
    readonly CaseLibrary _library;

    public CaseManagementTests() => _library = new CaseLibrary(Path.Combine(_tmp.Path, "Cases"));

    public void Dispose() => _tmp.Dispose();

    static List<LogEvent> AllEvents(Case c) => c.GetEvents(1, int.MaxValue).ToList();

    [Fact]
    public void 案件資料夾中有說明檔_提醒勿外流與關閉後再複製備份()
    {
        using var c = _library.Create("說明", PlantConfig.Default);

        var text = File.ReadAllText(Path.Combine(c.Folder, Case.ReadmeFileName));

        Assert.Contains("請勿外流", text);
        Assert.Contains("先在 LogTrace 中關閉這個案件", text);
    }

    static long Pragma(string db, string sql)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar() is long n ? n : 0;
    }

    [Fact]
    public void 新案件以8KB分頁建立()
    {
        string db;
        using (var c = _library.Create("分頁", PlantConfig.Default))
        {
            c.Import(TempFolder.Fixture("multi-day.zip"));
            db = Path.Combine(c.Folder, Case.DatabaseFileName);
        }

        // 長的多行事件跨頁存放時，4 KB 分頁浪費空間（#51：真實樣本 4 KB 60 MB、8 KB 45 MB）
        Assert.Equal(8192, Pragma(db, "PRAGMA page_size"));
    }

    [Fact]
    public void 以4KB分頁建立的舊案件_照常開啟_匯入與搜尋_分頁不變()
    {
        string folder, db;
        using (var c = _library.Create("舊案件", PlantConfig.Default))
            (folder, db) = (c.Folder, Path.Combine(c.Folder, Case.DatabaseFileName));
        Pragma(db, "PRAGMA journal_mode=DELETE; PRAGMA page_size=4096; VACUUM; PRAGMA journal_mode=WAL;"); // 同一個連線內才有效
        Assert.Equal(4096, Pragma(db, "PRAGMA page_size"));

        using (var c = Case.Open(folder))
        {
            c.Import(TempFolder.Fixture("multi-day.zip"));
            Assert.NotEmpty(AllEvents(c));
            Assert.NotEmpty(c.Search(new Search.SearchQuery { AllOf = "CMD_NO" }));
        }

        Assert.Equal(4096, Pragma(db, "PRAGMA page_size"));
    }

    [Fact]
    public void 關閉案件後複製整個資料夾_在另一處可以開啟_內容相同()
    {
        string folder;
        List<LogEvent> before;
        using (var c = _library.Create("可攜", PlantConfig.Default))
        {
            c.Import(TempFolder.Fixture("multi-day.zip"));
            before = AllEvents(c);
            folder = c.Folder;
        }
        Assert.Empty(Directory.EnumerateFiles(folder, "*-wal"));

        var copy = Path.Combine(_tmp.Path, "別台電腦", "可攜");
        foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(copy, Path.GetRelativePath(folder, f));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target);
        }

        using var opened = Case.Open(copy);
        Assert.Equal(before, AllEvents(opened));
        var e = before.First(x => x.Module == "PLC");
        Assert.NotEmpty(opened.ReadRawLines(e.Source.RelativePath, e.Source.LineStart, 1)[0].Text);
    }

    [Fact]
    public void 稽核紀錄包含時間_操作人_操作種類與對象()
    {
        var start = DateTimeOffset.Now.AddSeconds(-1);
        using var c = _library.Create("稽核", PlantConfig.Default);
        c.Import(TempFolder.Fixture("one-day-plc"));

        var audit = c.GetAudit();

        Assert.Equal(["建立案件", "匯入"], audit.Select(a => a.Action));
        Assert.All(audit, a =>
        {
            Assert.InRange(a.Time, start, DateTimeOffset.Now.AddSeconds(1));
            Assert.Equal($@"{Environment.UserDomainName}\{Environment.UserName}", a.User);
            Assert.NotEmpty(a.Target);
        });
        Assert.Equal(TempFolder.Fixture("one-day-plc"), audit[1].Target);
        Assert.Contains("新增 1", audit[1].Detail);
    }

    [Fact]
    public void 案件清單列出存放位置下的案件()
    {
        using (_library.Create("乙案件", PlantConfig.Default)) { }
        using (_library.Create("甲案件", PlantConfig.Default)) { }
        Directory.CreateDirectory(Path.Combine(_library.Root, "不是案件"));

        var list = _library.List();

        Assert.Equal(2, list.Count);
        Assert.Contains(list, s => s.Name == "甲案件");
        Assert.Contains(list, s => s.Name == "乙案件");
    }

    [Fact]
    public void 刪除案件_移除案件資料夾_不影響原本的LOG_並記錄稽核()
    {
        var source = TempFolder.Fixture("one-day-plc");
        var sourceFiles = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
        string folder;
        using (var c = _library.Create("要刪除", PlantConfig.Default))
        {
            c.Import(source);
            folder = c.Folder;
        }

        _library.Delete(folder); // 原始檔複本為唯讀，也要能刪除

        Assert.False(Directory.Exists(folder));
        Assert.All(sourceFiles, f => Assert.True(File.Exists(f)));
        Assert.Empty(_library.List());
        var audit = _library.GetAudit();
        Assert.Equal(["建立案件", "刪除案件"], audit.Select(a => a.Action));
        Assert.Equal(folder, audit[1].Target);
    }

    [Fact]
    public void 刪除案件_只接受存放位置下的案件資料夾()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_tmp.Path, "外面")).FullName;
        File.WriteAllText(Path.Combine(outside, Case.DatabaseFileName), "");
        var notCase = Directory.CreateDirectory(Path.Combine(_library.Root, "不是案件")).FullName;

        Assert.Throws<InvalidOperationException>(() => _library.Delete(outside));
        Assert.Throws<InvalidOperationException>(() => _library.Delete(notCase));
        Assert.True(Directory.Exists(outside));
        Assert.True(Directory.Exists(notCase));
    }

    const string Home = @"C:\Users\虛構";

    [Theory]
    [InlineData(Home + @"\OneDrive\LogTrace\Cases", true)]           // 個人 OneDrive 的同步根目錄下
    [InlineData(Home + @"\OneDrive - 虛構公司\LogTrace", true)]      // 企業帳號的同步根目錄下
    [InlineData(@"E:\同步\OneDrive - 虛構公司\Cases", true)]         // 偵測不到同步根目錄，但資料夾名稱是 OneDrive
    [InlineData(Home + @"\AppData\Local\LogTrace\Cases", false)]
    [InlineData(@"D:\Cases", false)]
    public void 案件位置在OneDrive同步資料夾內時_判定為需要警告(string path, bool expected)
    {
        string[] roots = [Home + @"\OneDrive", Home + @"\OneDrive - 虛構公司"];

        Assert.Equal(expected, OneDriveDetector.IsInOneDrive(path, roots, []));
    }

    [Fact]
    public void 企業將已知資料夾移到OneDrive時_文件資料夾下的位置也要警告()
    {
        // Known Folder Move：「文件」的實際位置變成同步根目錄下的資料夾（虛構路徑）
        const string root = @"E:\Sync\Corp";
        const string documents = root + @"\Documents";

        Assert.True(OneDriveDetector.IsInOneDrive(documents + @"\LogTrace\Cases", [root], [documents]));
        // 偵測不到同步根目錄時，只要已知資料夾的路徑經過 OneDrive 資料夾，也要警告
        Assert.True(OneDriveDetector.IsInOneDrive(@"E:\X\OneDrive - 虛構公司\Documents\LogTrace", [], [@"E:\X\OneDrive - 虛構公司\Documents"]));
        // 沒有移到 OneDrive 的「文件」不警告
        Assert.False(OneDriveDetector.IsInOneDrive(Home + @"\Documents\LogTrace", [root], [Home + @"\Documents"]));
    }
}
