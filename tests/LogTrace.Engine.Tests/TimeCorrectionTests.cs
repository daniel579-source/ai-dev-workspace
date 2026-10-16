using System.Security.Cryptography;
using LogTrace.Engine.Cases;
using LogTrace.Engine.Events;
using LogTrace.Engine.Plants;
using LogTrace.Engine.Storage;
using LogTrace.Tests.Support;

namespace LogTrace.Engine.Tests;

// 時區與時間偏移量（#19，ADR-0003 第 1、5 點、ADR-0004 第 5 點）。
// 測試資料：tests/fixtures/two-modules（虛構）：EXE 09:00:00、09:00:05；PLC 09:00:00、09:00:01
public sealed class TimeCorrectionTests : IDisposable
{
    static readonly DateTime T0 = new(2030, 1, 7, 9, 0, 0);

    readonly TempFolder _tmp = new();
    readonly CaseLibrary _library;

    public TimeCorrectionTests() => _library = new CaseLibrary(Path.Combine(_tmp.Path, "Cases"));

    public void Dispose() => _tmp.Dispose();

    static List<LogEvent> AllEvents(Case c) => c.GetEvents(1, int.MaxValue).ToList();

    static string Order(Case c) => string.Join(" ", AllEvents(c).Select(e => $"{e.Module}@{(e.Timestamp - T0).TotalSeconds}"));

    static TimeCorrection Offsets(double caseSeconds, params (string Module, double Seconds)[] modules) =>
        TimeCorrection.Create(TimeSpan.FromSeconds(caseSeconds), modules.Select(m => KeyValuePair.Create(m.Module, TimeSpan.FromSeconds(m.Seconds))));

    Case Open()
    {
        var c = _library.Create("偏移量", PlantConfig.Default);
        c.Import(TempFolder.Fixture("two-modules"));
        return c;
    }

    static Dictionary<string, string> RawHashes(Case c) =>
        Directory.EnumerateFiles(c.RawFolder, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    [Fact]
    public void 整體偏移量_所有事件加上同一個常數_排序不變_標示已校正_原始時間字串不變()
    {
        using var c = Open();
        var before = AllEvents(c);
        Assert.Equal("EXE@0 PLC@0 PLC@1 EXE@5", Order(c));

        Assert.True(c.SetTimeCorrection(Offsets(5)));

        var after = AllEvents(c);
        Assert.Equal("EXE@5 PLC@5 PLC@6 EXE@10", Order(c));
        Assert.Equal(before.Select(e => e.Id), after.Select(e => e.Id)); // 排序不變
        Assert.Equal(before.Select(e => e.RawTimestamp), after.Select(e => e.RawTimestamp));
        Assert.All(after, e => Assert.True(e.Flags.HasFlag(EventFlags.Corrected)));
        Assert.Equal(before.Select(e => e.Flags | EventFlags.Corrected), after.Select(e => e.Flags)); // 同時間戳記等旗標不變
    }

    [Fact]
    public void 模組偏移量_改變跨模組的排序_只有該模組標示已校正()
    {
        using var c = Open();

        c.SetTimeCorrection(Offsets(0, ("PLC", 4.5)));

        Assert.Equal("EXE@0 PLC@4.5 EXE@5 PLC@5.5", Order(c));
        var events = AllEvents(c);
        Assert.Equal([1L, 2, 3, 4], events.Select(e => e.Sequence)); // 重新編事件序號
        Assert.All(events, e => Assert.Equal(e.Module == "PLC", e.Flags.HasFlag(EventFlags.Corrected)));
        Assert.DoesNotContain(events, e => e.Flags.HasFlag(EventFlags.SameTimestamp)); // 原本 09:00:00 的兩筆不再同時
    }

    [Fact]
    public void 模組偏移量造成時間相同時_依模組固定順序_標示同時間戳記()
    {
        using var c = Open();

        c.SetTimeCorrection(Offsets(1, ("PLC", 4))); // 整體 +1、PLC 再 +4：PLC 09:00:01 → 09:00:06，與 EXE 09:00:05 → 09:00:06 相同

        Assert.Equal("EXE@1 PLC@5 EXE@6 PLC@6", Order(c)); // 同時間依模組順序 EXE → PLC（ADR-0004 第 5 點）
        var events = AllEvents(c);
        Assert.Equal([false, false, true, true], events.Select(e => e.Flags.HasFlag(EventFlags.SameTimestamp)));
    }

    [Fact]
    public void 偏移量不改變原始資料_改回0後完全恢復()
    {
        using var c = Open();
        var before = AllEvents(c);
        var hashes = RawHashes(c);

        c.SetTimeCorrection(Offsets(-3.25, ("EXE", 7)));
        Assert.Equal(hashes, RawHashes(c));

        c.SetTimeCorrection(TimeCorrection.None);
        Assert.Equal(before, AllEvents(c));
        Assert.Equal(hashes, RawHashes(c));
    }

    [Fact]
    public void 偏移量的修改寫入稽核紀錄_舊值新值時間修改人()
    {
        using var c = Open();
        var start = DateTimeOffset.Now.AddSeconds(-1);

        c.SetTimeCorrection(Offsets(5));
        c.SetTimeCorrection(Offsets(5, ("PLC", -1.5)));
        Assert.False(c.SetTimeCorrection(Offsets(5, ("PLC", -1.5)))); // 沒有改變：不重建、不記錄

        var audit = c.GetAudit().Where(a => a.Action == "設定時間偏移量").ToList();
        Assert.Equal(2, audit.Count);
        Assert.Contains(audit, a => a.Detail.StartsWith("無 → 整體 +5 秒"));
        Assert.Contains(audit, a => a.Detail.StartsWith("整體 +5 秒 → 整體 +5 秒；PLC −1.5 秒"));
        Assert.All(audit, a => Assert.Equal(AuditEntry.CurrentUser, a.User));
        Assert.All(audit, a => Assert.True(a.Time >= start));
    }

    [Fact]
    public void 偏移量保存在案件中_重新開啟後仍有效_之後的匯入也套用()
    {
        string folder;
        using (var c = Open())
        {
            c.SetTimeCorrection(Offsets(0, ("PLC", 4.5)));
            folder = c.Folder;
        }

        using var reopened = Case.Open(folder);
        Assert.Equal(TimeSpan.FromSeconds(4.5), reopened.Info.Correction.For("PLC"));
        Assert.Equal(TimeSpan.Zero, reopened.Info.Correction.For("EXE"));

        reopened.Import(TempFolder.Fixture("multi-day"));
        Assert.All(AllEvents(reopened).Where(e => e.Module == "PLC"), e => Assert.True(e.Flags.HasFlag(EventFlags.Corrected)));
        Assert.All(AllEvents(reopened).Where(e => e.Module != "PLC"), e => Assert.False(e.Flags.HasFlag(EventFlags.Corrected)));
    }

    [Theory]
    [InlineData("5", 50_000_000L)]
    [InlineData("+1.25", 12_500_000L)]
    [InlineData("−0.0001", -1_000L)]
    [InlineData("-3 秒", -30_000_000L)]
    [InlineData("", 0L)]
    public void 偏移量的輸入以秒為單位_精度100微秒(string text, long ticks) =>
        Assert.Equal(TimeSpan.FromTicks(ticks), TimeCorrection.ParseSeconds(text));

    [Theory]
    [InlineData("abc")]
    [InlineData("0.00001")] // 小於 100 微秒
    [InlineData("86401")]   // 超過一天
    public void 無法使用的偏移量輸入(string text) => Assert.Null(TimeCorrection.ParseSeconds(text));

    [Fact]
    public void 偏移量的文字()
    {
        Assert.Equal("+5 秒", TimeCorrection.Format(TimeSpan.FromSeconds(5)));
        Assert.Equal("−1.25 秒", TimeCorrection.Format(TimeSpan.FromSeconds(-1.25)));
        Assert.Equal("0", TimeCorrection.Format(TimeSpan.Zero));
        Assert.Equal("無", TimeCorrection.None.Describe());
        Assert.True(Offsets(0, ("PLC", 0)).IsZero);
        Assert.Throws<ArgumentOutOfRangeException>(() => Offsets(86_401));
    }

    [Fact]
    public void 時區_預設台北_保存在案件中_認不得的識別碼註明()
    {
        Assert.Equal("Asia/Taipei", new PlantConfigDocument().TimeZone);
        Assert.Equal("Asia/Taipei（UTC+08:00）", PlantConfig.TimeZoneLabel("Asia/Taipei"));
        Assert.Equal("Tokyo Standard Time（UTC+09:00）", PlantConfig.TimeZoneLabel("Tokyo Standard Time"));
        Assert.Contains("無法辨識", PlantConfig.TimeZoneLabel("Mars/Base"));

        var doc = new PlantConfigDocument { Plant = "範例廠", TimeZone = "Tokyo Standard Time" };
        string folder;
        using (var c = _library.Create("時區", doc.ToPlantConfig())) folder = c.Folder;
        using var reopened = Case.Open(folder);
        Assert.Equal("Tokyo Standard Time", reopened.Info.Plant.TimeZone);
    }
}
