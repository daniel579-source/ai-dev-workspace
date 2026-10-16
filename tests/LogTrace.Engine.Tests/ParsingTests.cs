using System.Text;
using LogTrace.Engine.Cases;
using LogTrace.Engine.Events;
using LogTrace.Engine.Parsing;
using LogTrace.Engine.Plants;
using LogTrace.Tests.Support;

namespace LogTrace.Engine.Tests;

// 解析狀況（ADR-0004）。測試資料：tests/fixtures/parsing-cases（虛構；PLC 為 Big5、Server 為 UTF-8）
public sealed class ParsingTests : IDisposable
{
    const string PlcArchive = "20300108/PLC-2030-01-08/archives/PLCLOG_2030-01-08 08_00_02.0000.txt";
    const string PlcCurrent = "20300108/PLC-2030-01-08/PLCLOG.txt";
    const string WcsCmd = "20300108/Server-2030-01-08/WCSCMD.txt";
    const string Mystery = "20300108/Server-2030-01-08/MysteryLOG.txt";
    const string Notes = "20300108/Server-2030-01-08/notes.txt";

    readonly TempFolder _tmp = new();
    readonly CaseLibrary _library;

    public ParsingTests() => _library = new CaseLibrary(Path.Combine(_tmp.Path, "Cases"));

    public void Dispose() => _tmp.Dispose();

    static List<LogEvent> AllEvents(Case c) => c.GetEvents(1, int.MaxValue).ToList();

    Case ImportParsingCases()
    {
        var c = _library.Create("解析狀況", PlantConfig.Default);
        c.Import(TempFolder.Fixture("parsing-cases"));
        return c;
    }

    static LogEvent At(List<LogEvent> events, string path, int line) =>
        events.Single(e => e.Source.RelativePath == path && e.Source.LineStart == line);

    static DateTime T(int s, int frac) => new DateTime(2030, 1, 8, 8, 0, s).AddTicks(frac * 1000L);

    [Fact]
    public void 所有事件依時間與模組順序排列_輪替檔封存在前()
    {
        using var c = ImportParsingCases();

        var order = AllEvents(c).Select(e => (e.Source.RelativePath, e.Source.LineStart));

        Assert.Equal(
        [
            (PlcArchive, 1), (PlcArchive, 3), (WcsCmd, 1),
            (PlcCurrent, 1), (PlcCurrent, 3),
            (PlcArchive, 5),
            (PlcCurrent, 5),
            (WcsCmd, 3),
            (Mystery, 1),
        ], order);
    }

    [Fact]
    public void Big5_與_UTF8_依檔案判斷並正確解碼()
    {
        using var c = ImportParsingCases();
        var events = AllEvents(c);

        Assert.Equal("[A-03][Unload] => 卸貨完成 CMD_NO=00020001\n詳細：虛構延續行", At(events, PlcCurrent, 5).Message);
        Assert.Equal("加入命令 CMD_NO=00020001", At(events, WcsCmd, 1).Message);
        var encodings = c.GetFiles().ToDictionary(f => f.RelativePath, f => f.Encoding);
        Assert.Equal(LogEncoding.Big5Name, encodings[PlcCurrent]);
        Assert.Equal(LogEncoding.Big5Name, encodings[PlcArchive]);
        Assert.Equal(LogEncoding.Utf8Name, encodings[WcsCmd]);
    }

    [Fact]
    public void 無法解碼的位元組_事件保留並標示編碼錯誤()
    {
        using var c = ImportParsingCases();
        var events = AllEvents(c);

        var bad = At(events, PlcArchive, 5);
        Assert.True(bad.Flags.HasFlag(EventFlags.EncodingError));
        Assert.StartsWith("[A-01][Unload] => 卸貨", bad.Message);
        Assert.All(events.Where(e => e != bad), e => Assert.False(e.Flags.HasFlag(EventFlags.EncodingError)));
        // 原始位元組保留在原始檔複本
        var raw = File.ReadAllBytes(Path.Combine(c.RawFolder, PlcArchive));
        Assert.Equal(0xFF, raw[(int)bad.Source.ByteOffset + bad.Source.ByteLength - 2]);
    }

    [Fact]
    public void 延續行併入前一筆事件()
    {
        using var c = ImportParsingCases();

        var ex = At(AllEvents(c), WcsCmd, 3);

        Assert.Equal((3, 5), (ex.Source.LineStart, ex.Source.LineEnd));
        Assert.Equal("System.Exception: 虛構例外\n   at Fake.Method() in Fake.cs:line 1\n   at Fake.Other()", ex.Message);
        Assert.Equal("ERROR", ex.Level);
    }

    [Fact]
    public void 時間戳記無效時_沿用前一筆有效事件的時間並標示()
    {
        using var c = ImportParsingCases();

        var invalid = At(AllEvents(c), PlcCurrent, 3);

        Assert.True(invalid.Flags.HasFlag(EventFlags.TimeInvalid));
        Assert.Equal("2030-13-40 08:00:03.0000", invalid.RawTimestamp);
        Assert.Equal(T(1, 9990), invalid.Timestamp);
    }

    [Fact]
    public void 檔案開頭的前置內容_保存為事件並標示時間無效與未知種類()
    {
        using var c = ImportParsingCases();

        var preamble = At(AllEvents(c), PlcArchive, 1);

        Assert.Equal("# 虛構前置內容：檔案開頭沒有時間戳記", preamble.Message);
        Assert.True(preamble.Flags.HasFlag(EventFlags.TimeInvalid));
        Assert.True(preamble.Flags.HasFlag(EventFlags.UnknownKind));
        Assert.Equal(T(0, 0), preamble.Timestamp); // 前面沒有有效事件：沿用後面第一筆的時間
        Assert.Equal("PLCLOG", preamble.Logger);
    }

    [Fact]
    public void 輪替檔接續_跨檔案的時間倒退也會標示()
    {
        using var c = ImportParsingCases();
        var events = AllEvents(c);

        var first = At(events, PlcCurrent, 1);

        Assert.True(first.Flags.HasFlag(EventFlags.TimeBackward)); // 現行檔第一筆早於封存檔最後一筆
        Assert.Equal(T(1, 9990), first.Timestamp);
        Assert.Single(events, e => e.Flags.HasFlag(EventFlags.TimeBackward));
    }

    [Fact]
    public void 同一時間戳記多筆_跨模組依模組固定順序並標示()
    {
        using var c = ImportParsingCases();
        var events = AllEvents(c);

        var same = events.Where(e => e.Timestamp == T(0, 0)).ToList();

        Assert.Equal(["PLC", "PLC", "Server"], same.Select(e => e.Module));
        Assert.True(At(events, PlcArchive, 3).Flags.HasFlag(EventFlags.SameTimestamp));
        Assert.True(At(events, WcsCmd, 1).Flags.HasFlag(EventFlags.SameTimestamp));
        Assert.False(At(events, WcsCmd, 3).Flags.HasFlag(EventFlags.SameTimestamp));
    }

    [Fact]
    public void 時間為沿用的事件_不算進同時間戳記()
    {
        using var c = ImportParsingCases();
        var events = AllEvents(c);

        // 第 3 行沿用第 1 行的時間；兩者都不標示「同時間戳記」
        Assert.False(At(events, PlcCurrent, 1).Flags.HasFlag(EventFlags.SameTimestamp));
        Assert.False(At(events, PlcCurrent, 3).Flags.HasFlag(EventFlags.SameTimestamp));
        Assert.False(At(events, PlcArchive, 1).Flags.HasFlag(EventFlags.SameTimestamp));
    }

    [Fact]
    public void 未知的_LOG_種類_仍然保存為事件並標示()
    {
        using var c = ImportParsingCases();
        var events = AllEvents(c);

        var mystery = At(events, Mystery, 1);

        Assert.True(mystery.Flags.HasFlag(EventFlags.UnknownKind));
        Assert.Equal("未知種類的虛構紀錄", mystery.Message);
        Assert.False(At(events, WcsCmd, 1).Flags.HasFlag(EventFlags.UnknownKind));
    }

    [Fact]
    public void 整個檔案無法解析時_不產生事件_在檔案清單中標示()
    {
        using var c = ImportParsingCases();

        var notes = c.GetFiles().Single(f => f.RelativePath == Notes);

        Assert.True(notes.Unparsable);
        Assert.Equal(0, notes.EventCount);
        Assert.DoesNotContain(AllEvents(c), e => e.Source.RelativePath == Notes);
        Assert.Equal(["這不是 LOG", "只是虛構的說明文字"], c.ReadRawLines(Notes, 1, 10).Select(l => l.Text));
    }

    [Fact]
    public void 匯入後沒有任何一行原始內容遺失_每個非空白行都屬於某筆事件()
    {
        using var c = ImportParsingCases();
        var events = AllEvents(c);

        foreach (var file in c.GetFiles().Where(f => !f.Unparsable))
        {
            var lines = c.ReadRawLines(file.RelativePath, 1, int.MaxValue);
            var covered = events.Where(e => e.Source.RelativePath == file.RelativePath)
                .SelectMany(e => Enumerable.Range(e.Source.LineStart, e.Source.LineEnd - e.Source.LineStart + 1))
                .ToList();
            Assert.Equal(covered.Count, covered.Distinct().Count()); // 事件之間沒有重疊
            var nonBlank = lines.Where(l => l.Text.Trim().Length > 0).Select(l => l.LineNo);
            Assert.All(nonBlank, n => Assert.Contains(n, covered));
            Assert.Equal(file.EventCount, events.Count(e => e.Source.RelativePath == file.RelativePath));
        }
    }

    [Fact]
    public void 同一份資料匯入兩次_事件順序與穩定識別碼完全相同()
    {
        using var a = _library.Create("第一次", PlantConfig.Default);
        using var b = _library.Create("第二次", PlantConfig.Default);
        a.Import(TempFolder.Fixture("parsing-cases"));
        b.Import(TempFolder.Fixture("parsing-cases"));

        Assert.Equal(AllEvents(a), AllEvents(b));
    }

    [Fact]
    public void 原始_LOG_依原始行序讀取_時間倒退的行維持原位()
    {
        using var c = ImportParsingCases();

        var lines = c.ReadRawLines(PlcCurrent, 1, 3);

        Assert.Equal([1, 2, 3], lines.Select(l => l.LineNo));
        Assert.Equal("2030-01-08 08:00:01.9990 | DEBUG | PLCLOG | [A-02][Station_Status_Change] => 在席 = OFF  ", lines[0].Text);
        Assert.Equal("", lines[1].Text);
        Assert.Equal(7, c.GetRawLineCount(PlcCurrent));
    }

    [Fact]
    public void 解析器_只有前置內容沒有時間戳記的資料_視為無法解析()
    {
        var result = LogRecordParser.Parse(Encoding.UTF8.GetBytes("虛構說明\r\n第二行\r\n"));

        Assert.True(result.Unparsable);
        Assert.Empty(result.Events);
    }

    [Fact]
    public void 解析器_Windows_的_Big5_解碼器把_0xFF_變成私用區字元時也標示編碼錯誤()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(950).GetBytes("2030-01-08 08:00:00.0000 | DEBUG | PLCLOG | 路徑規劃")
            .Concat(new byte[] { 0xFF, 0x0D, 0x0A }).ToArray();

        var result = LogRecordParser.Parse(bytes);

        Assert.Equal(LogEncoding.Big5Name, result.Encoding);
        Assert.True(result.Events.Single().Flags.HasFlag(EventFlags.EncodingError));
    }

    [Fact]
    public void 解析器_無法解碼的位元組不會變成一般的問號()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        // 0xFE 是 Big5 的前導位元組，後面接的不是有效的第二個位元組
        var bytes = Encoding.GetEncoding(950).GetBytes("2030-01-08 08:00:00.0000 | DEBUG | PLCLOG | 卸貨")
            .Concat(new byte[] { 0xFE, 0x20, 0x41, 0x0D, 0x0A }).ToArray();

        var e = LogRecordParser.Parse(bytes).Events.Single();

        Assert.True(e.Flags.HasFlag(EventFlags.EncodingError));
        Assert.DoesNotContain('?', e.Message);
    }
}
