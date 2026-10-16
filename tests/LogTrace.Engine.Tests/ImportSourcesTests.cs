using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using LogTrace.Engine.Cases;
using LogTrace.Engine.Events;
using LogTrace.Engine.Import;
using LogTrace.Engine.Plants;
using LogTrace.Tests.Support;

namespace LogTrace.Engine.Tests;

// 匯入來源（資料夾、zip、7z）、多天、重複匯入（ADR-0002）。測試資料：tests/fixtures（虛構）
public sealed class ImportSourcesTests : IDisposable
{
    const string PlcLog = "20300107/PLC-2030-01-07/PLCLOG.txt";

    readonly TempFolder _tmp = new();
    readonly CaseLibrary _library;

    public ImportSourcesTests() => _library = new CaseLibrary(Path.Combine(_tmp.Path, "Cases"));

    public void Dispose() => _tmp.Dispose();

    static List<LogEvent> AllEvents(Case c) => c.GetEvents(1, int.MaxValue).ToList();

    static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>把 fixture 複製到暫存資料夾，讓測試可以修改來源。</summary>
    string CopyFixture(string name)
    {
        var dest = Path.Combine(_tmp.Path, "src-" + Guid.NewGuid().ToString("N")[..8]);
        foreach (var f in Directory.EnumerateFiles(TempFolder.Fixture(name), "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dest, Path.GetRelativePath(TempFolder.Fixture(name), f));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target);
        }
        return dest;
    }

    [Theory]
    [InlineData("multi-day.zip")]
    [InlineData("multi-day.7z")]
    public void 資料夾_zip_7z_三種來源的匯入結果相同(string archive)
    {
        using var fromFolder = _library.Create("資料夾", PlantConfig.Default);
        using var fromArchive = _library.Create("壓縮檔", PlantConfig.Default);

        fromFolder.Import(TempFolder.Fixture("multi-day"));
        var result = fromArchive.Import(TempFolder.Fixture(archive));

        Assert.Equal(AllEvents(fromFolder), AllEvents(fromArchive));
        Assert.Equal(
            fromFolder.GetFiles().Select(f => (f.RelativePath, f.Sha256, f.EventCount)),
            fromArchive.GetFiles().Select(f => (f.RelativePath, f.Sha256, f.EventCount)));
        Assert.Equal(7, result.FileCount);
        Assert.Equal(7, result.NewFiles);
    }

    [Fact]
    public void 壓縮檔整份保存在案件中_暫存區在匯入後清除()
    {
        using var c = _library.Create("壓縮檔", PlantConfig.Default);
        var archive = TempFolder.Fixture("multi-day.7z");

        c.Import(archive);

        var import = c.GetImports().Single();
        Assert.Equal("7z", import.SourceKind);
        Assert.Equal(Sha(archive), import.ArchiveSha256);
        var copy = Path.Combine(c.Folder, "sources", import.ArchiveCopy!);
        Assert.Equal(Sha(archive), Sha(copy));
        Assert.False(Directory.Exists(Path.Combine(c.Folder, "staging", import.Id.ToString())));
        Assert.All(c.GetFiles(), f => Assert.StartsWith(archive + "|logs/", f.SourcePath));
    }

    [Fact]
    public void 一次匯入多天_所有事件依時間排序()
    {
        using var c = _library.Create("多天", PlantConfig.Default);

        c.Import(TempFolder.Fixture("multi-day"));

        var events = AllEvents(c);
        Assert.Equal(["20300107", "20300108"], events.Select(e => e.Id.DayFolder).Distinct());
        Assert.Equal(13, events.Count); // two-modules 4 筆＋parsing-cases 9 筆
        Assert.Equal(events.OrderBy(e => e.Timestamp).Select(e => e.Sequence), events.Select(e => e.Sequence));
    }

    [Fact]
    public void 重複匯入完全相同的資料_全部略過_原始檔複本不被覆寫()
    {
        using var c = _library.Create("重複", PlantConfig.Default);
        c.Import(TempFolder.Fixture("one-day-plc"));
        var before = AllEvents(c);
        var raw = Path.Combine(c.RawFolder, PlcLog);
        var written = File.GetLastWriteTimeUtc(raw);

        var result = c.Import(TempFolder.Fixture("one-day-plc"));

        Assert.Equal((0, 1, 0, 0, 0), (result.NewFiles, result.IdenticalFiles, result.ExtendedFiles, result.ConflictsUsedNew, result.ConflictsKeptOld));
        Assert.Equal(before, AllEvents(c));
        Assert.Single(c.GetFiles());
        Assert.Equal(written, File.GetLastWriteTimeUtc(raw));
    }

    [Fact]
    public void 新檔案_加入案件並重新排序()
    {
        using var c = _library.Create("新增", PlantConfig.Default);
        c.Import(TempFolder.Fixture("one-day-plc"));

        var result = c.Import(TempFolder.Fixture("two-modules"));

        // two-modules 的 PLCLOG 與 one-day-plc 同名但內容不同（衝突）；EXEDebug 是新檔案
        Assert.Equal(1, result.NewFiles);
        Assert.Equal(1, result.ConflictsKeptOld);
        Assert.Contains(AllEvents(c), e => e.Module == "EXE");
        Assert.Equal(Enumerable.Range(1, AllEvents(c).Count).Select(i => (long)i), AllEvents(c).Select(e => e.Sequence));
    }

    [Fact]
    public void 舊檔是新檔的開頭部分時_自動改用新檔_原有事件的識別碼與行號不變()
    {
        var source = CopyFixture("one-day-plc");
        using var c = _library.Create("重新下載", PlantConfig.Default);
        c.Import(source);
        var before = AllEvents(c);
        var v1 = Path.Combine(c.RawFolder, PlcLog);
        var v1Hash = Sha(v1);

        // 模擬晚一點重新下載：同一檔案多了後半段（虛構）
        File.AppendAllText(Path.Combine(source, PlcLog), "2030-01-07 08:00:03.0000 | DEBUG | PLCLOG | [A-04][Station_Status_Change] => 在席 = ON  \r\n\r\n", Encoding.UTF8);
        var result = c.Import(source);

        Assert.Equal(1, result.ExtendedFiles);
        var after = AllEvents(c);
        Assert.Equal(before.Count + 1, after.Count);
        Assert.All(before, b => Assert.Contains(after, a => a.Id == b.Id && a.Source == b.Source && a.Message == b.Message));
        var files = c.GetFiles();
        Assert.Equal([(1, false), (2, true)], files.Select(f => (f.Version, f.Active)));
        Assert.Equal(v1Hash, Sha(v1)); // 舊版本保留、未被覆寫
        Assert.Equal("_versions/2/" + PlcLog, files[1].StoragePath);
        Assert.Contains(c.GetAudit(), a => a.Action == "重複匯入" && a.Target == PlcLog);
        Assert.Equal(14, c.GetRawLineCount(PlcLog)); // 原始 LOG 面板讀新版本（原本 12 行＋新增 2 行）
    }

    [Fact]
    public void 同名但內容衝突_預設保留舊檔_新檔另存為新版本()
    {
        var source = CopyFixture("one-day-plc");
        using var c = _library.Create("衝突", PlantConfig.Default);
        c.Import(source);
        var before = AllEvents(c);
        var path = Path.Combine(source, PlcLog);
        File.WriteAllText(path, File.ReadAllText(path).Replace("在席 = ON", "在席 = OFF"), Encoding.UTF8);

        using var prepared = c.PrepareImport(source);
        Assert.Equal(PlcLog, prepared.Conflicts.Single().RelativePath);
        var result = c.ApplyImport(prepared);

        Assert.Equal(1, result.ConflictsKeptOld);
        Assert.Equal(before, AllEvents(c));
        Assert.Equal([(1, true), (2, false)], c.GetFiles().Select(f => (f.Version, f.Active)));
        Assert.True(File.Exists(Path.Combine(c.RawFolder, "_versions", "2", PlcLog)));
        Assert.Contains(c.GetAudit(), a => a.Action == "重複匯入的選擇" && a.Target == PlcLog && a.Detail.Contains("保留舊檔"));
    }

    [Fact]
    public void 同名但內容衝突_選擇改用新檔時_查詢改用新版本_舊檔保留()
    {
        var source = CopyFixture("one-day-plc");
        using var c = _library.Create("衝突", PlantConfig.Default);
        c.Import(source);
        var path = Path.Combine(source, PlcLog);
        File.WriteAllText(path, File.ReadAllText(path).Replace("在席 = ON", "在席 = OFF"), Encoding.UTF8);

        using var prepared = c.PrepareImport(source);
        var result = c.ApplyImport(prepared, new HashSet<string> { PlcLog });

        Assert.Equal(1, result.ConflictsUsedNew);
        Assert.DoesNotContain(AllEvents(c), e => e.Message.Contains("在席 = ON"));
        Assert.Equal([(1, false), (2, true)], c.GetFiles().Select(f => (f.Version, f.Active)));
        Assert.True(File.Exists(Path.Combine(c.RawFolder, PlcLog)));
        Assert.Contains(c.GetAudit(), a => a.Action == "重複匯入的選擇" && a.Detail.Contains("改用新檔"));
    }

    [Fact]
    public void 準備後取消匯入_案件不變_壓縮檔複本與暫存區都清除()
    {
        using var c = _library.Create("取消", PlantConfig.Default);

        using (var prepared = c.PrepareImport(TempFolder.Fixture("multi-day.zip")))
            Assert.Equal(7, prepared.Items.Count);

        Assert.Equal(0, c.EventCount);
        Assert.Empty(c.GetFiles());
        Assert.DoesNotContain(Directory.EnumerateFiles(c.Folder, "*", SearchOption.AllDirectories),
            f => f.Contains(Path.DirectorySeparatorChar + "sources" + Path.DirectorySeparatorChar)
              || f.Contains(Path.DirectorySeparatorChar + "staging" + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void 壓縮檔中有跑出資料夾的路徑時_拒絕匯入且不寫出任何檔案()
    {
        var zip = Path.Combine(_tmp.Path, "evil.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using var w = new StreamWriter(archive.CreateEntry("../../evil.txt").Open());
            w.Write("虛構");
        }
        using var c = _library.Create("不安全", PlantConfig.Default);

        Assert.Throws<InvalidDataException>(() => c.Import(zip));

        Assert.Empty(Directory.EnumerateFiles(_tmp.Path, "evil.txt", SearchOption.AllDirectories));
        Assert.Empty(c.GetImports());
    }

    [Fact]
    public void 不是資料夾也不是壓縮檔時_回報錯誤()
    {
        var file = Path.Combine(_tmp.Path, "log.txt");
        File.WriteAllText(file, "虛構");
        using var c = _library.Create("錯誤", PlantConfig.Default);

        Assert.Throws<InvalidDataException>(() => c.Import(file));
        Assert.Throws<DirectoryNotFoundException>(() => c.Import(Path.Combine(_tmp.Path, "不存在")));
    }
}
