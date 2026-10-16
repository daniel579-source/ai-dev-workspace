using System.Security.Cryptography;
using LogTrace.Engine.Cases;
using LogTrace.Engine.Events;
using LogTrace.Engine.Plants;
using LogTrace.Tests.Support;

namespace LogTrace.Engine.Tests;

// 測試資料：tests/fixtures（虛構）
public sealed class ImportTests : IDisposable
{
    readonly TempFolder _tmp = new();
    readonly CaseLibrary _library;

    public ImportTests() => _library = new CaseLibrary(Path.Combine(_tmp.Path, "Cases"));

    public void Dispose() => _tmp.Dispose();

    static List<LogEvent> AllEvents(Case c) => c.GetEvents(1, int.MaxValue).ToList();

    [Fact]
    public void 匯入單日單一模組後_事件依時間排序並編上事件序號()
    {
        using var c = _library.Create("案件一", PlantConfig.Default);

        var result = c.Import(TempFolder.Fixture("one-day-plc"));

        Assert.Equal(1, result.FileCount);
        Assert.Equal(5, result.EventCount);
        var events = AllEvents(c);
        Assert.Equal([1L, 2, 3, 4, 5], events.Select(e => e.Sequence));
        // 時間倒退的事件（第 5 行）依時間排在最前面；同時間戳記依檔案內行號
        Assert.Equal([5, 1, 3, 7, 11], events.Select(e => e.Source.LineStart));
        Assert.True(events[0].Flags.HasFlag(EventFlags.TimeBackward));
        Assert.True(events[1].Flags.HasFlag(EventFlags.SameTimestamp));
        Assert.True(events[2].Flags.HasFlag(EventFlags.SameTimestamp));
        Assert.Equal(new DateTime(2030, 1, 7, 8, 0, 0, 5), events[0].Timestamp);
    }

    [Fact]
    public void 每筆事件保存穩定識別碼與原始位置_延續行屬於同一筆事件()
    {
        using var c = _library.Create("案件一", PlantConfig.Default);
        c.Import(TempFolder.Fixture("one-day-plc"));

        var unload = AllEvents(c).Single(e => e.Message.Contains("卸貨完成"));

        Assert.Equal(new StableEventId("PLC", "20300107", "20300107/PLC-2030-01-07/PLCLOG.txt", 7), unload.Id);
        Assert.Equal("20300107/PLC-2030-01-07/PLCLOG.txt", unload.Source.RelativePath);
        Assert.Equal((7, 9), (unload.Source.LineStart, unload.Source.LineEnd));
        Assert.Equal("[A-01][Unload] => 卸貨完成 CMD_NO=00010001\n詳細：虛構延續行一\n詳細：虛構延續行二", unload.Message);
        Assert.Equal("INFO", unload.Level);
        Assert.Equal("PLCLOG", unload.Logger);
        Assert.Equal("2030-01-07 08:00:01.2000", unload.RawTimestamp);
    }

    [Fact]
    public void 位元組位移可以從原始檔複本讀回事件的開頭()
    {
        using var c = _library.Create("案件一", PlantConfig.Default);
        c.Import(TempFolder.Fixture("one-day-plc"));
        var e = AllEvents(c).Single(x => x.Source.LineStart == 7);

        var bytes = File.ReadAllBytes(Path.Combine(c.RawFolder, e.Source.RelativePath));
        var text = System.Text.Encoding.UTF8.GetString(bytes, (int)e.Source.ByteOffset, e.Source.ByteLength);

        Assert.StartsWith("2030-01-07 08:00:01.2000 | INFO | PLCLOG |", text);
        Assert.EndsWith("詳細：虛構延續行二", text);
    }

    [Fact]
    public void 原始檔複本與案件資料庫分開存放_內容與匯入前完全相同()
    {
        var source = TempFolder.Fixture("one-day-plc");
        var sourceFile = Path.Combine(source, "20300107", "PLC-2030-01-07", "PLCLOG.txt");
        var hashBefore = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourceFile)));
        using var c = _library.Create("案件一", PlantConfig.Default);

        c.Import(source);

        var copy = Path.Combine(c.RawFolder, "20300107", "PLC-2030-01-07", "PLCLOG.txt");
        Assert.True(File.Exists(copy));
        Assert.True(File.Exists(Path.Combine(c.Folder, Case.DatabaseFileName)));
        Assert.NotEqual(Path.GetDirectoryName(copy), c.Folder);
        Assert.Equal(hashBefore, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(copy))));
        Assert.Equal(hashBefore, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourceFile)))); // 原始檔未被修改
        Assert.Equal(hashBefore, c.GetFiles().Single().Sha256);
    }

    [Fact]
    public void 同時間戳記跨模組時_依模組固定順序排列()
    {
        using var c = _library.Create("兩個模組", PlantConfig.Default);
        c.Import(TempFolder.Fixture("two-modules"));

        var events = AllEvents(c);

        Assert.Equal(["EXE", "PLC", "PLC", "EXE"], events.Select(e => e.Module));
        Assert.All(events.Take(2), e => Assert.True(e.Flags.HasFlag(EventFlags.SameTimestamp)));
    }

    [Fact]
    public void 重新開啟案件後_事件與順序相同()
    {
        string folder;
        List<LogEvent> before;
        using (var c = _library.Create("案件一", PlantConfig.Default))
        {
            c.Import(TempFolder.Fixture("one-day-plc"));
            before = AllEvents(c);
            folder = c.Folder;
        }

        using var reopened = Case.Open(folder);

        Assert.Equal("案件一", reopened.Info.Name);
        Assert.Equal(before, AllEvents(reopened));
    }

    [Fact]
    public void 匯入沒有日期與模組結構的資料夾時_回報錯誤且不建立事件()
    {
        var empty = Directory.CreateDirectory(Path.Combine(_tmp.Path, "not-a-log-folder")).FullName;
        File.WriteAllText(Path.Combine(empty, "readme.txt"), "虛構");
        using var c = _library.Create("案件一", PlantConfig.Default);

        Assert.Throws<InvalidDataException>(() => c.Import(empty));
        Assert.Equal(0, c.EventCount);
    }

    [Fact]
    public void 案件名稱重複時_建立不同的案件資料夾()
    {
        using var a = _library.Create("同名", PlantConfig.Default);
        using var b = _library.Create("同名", PlantConfig.Default);

        Assert.NotEqual(a.Folder, b.Folder);
        Assert.Equal("同名", b.Info.Name);
    }
}
