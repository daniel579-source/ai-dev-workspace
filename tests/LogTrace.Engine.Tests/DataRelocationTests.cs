using LogTrace.Engine.Cases;
using LogTrace.Tests.Support;

namespace LogTrace.Engine.Tests;

// 舊存放位置的資料搬到新位置（#57）。內容全部是虛構的。
public sealed class DataRelocationTests : IDisposable
{
    readonly TempFolder _tmp = new();

    public void Dispose() => _tmp.Dispose();

    string Write(string relative, string content)
    {
        var path = Path.Combine(_tmp.Path, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void 搬移後新位置內容完全相同_舊位置刪除()
    {
        Write(@"old\案件A\case.db", "虛構資料庫");
        Write(@"old\案件A\raw\PLC\PLCLOG.txt", "虛構 LOG");
        Write(@"old\audit-log.jsonl", "{}");

        var r = DataRelocation.Move(Path.Combine(_tmp.Path, "old"), Path.Combine(_tmp.Path, "new"));

        Assert.True(r.AllMoved);
        Assert.Equal(["audit-log.jsonl", "案件A"], r.Moved.Order(StringComparer.Ordinal));
        Assert.Equal("虛構 LOG", File.ReadAllText(Path.Combine(_tmp.Path, @"new\案件A\raw\PLC\PLCLOG.txt")));
        Assert.Equal("虛構資料庫", File.ReadAllText(Path.Combine(_tmp.Path, @"new\案件A\case.db")));
        Assert.False(Directory.Exists(Path.Combine(_tmp.Path, "old")));
    }

    [Fact]
    public void 新位置已有同名項目時不覆蓋_留在舊位置()
    {
        Write(@"old\範例廠.json", "舊的");
        Write(@"new\範例廠.json", "新的");

        var r = DataRelocation.Move(Path.Combine(_tmp.Path, "old"), Path.Combine(_tmp.Path, "new"));

        Assert.Equal(["範例廠.json"], r.Skipped);
        Assert.Equal("新的", File.ReadAllText(Path.Combine(_tmp.Path, @"new\範例廠.json")));
        Assert.Equal("舊的", File.ReadAllText(Path.Combine(_tmp.Path, @"old\範例廠.json")));
    }

    [Fact]
    public void 複製失敗時_舊位置保留原狀_新位置不留下不完整的複本()
    {
        var locked = Write(@"old\案件B\raw\locked.txt", "虛構");
        Write(@"old\案件B\case.db", "虛構資料庫");

        RelocationResult r;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None)) // 被其他程式鎖住
            r = DataRelocation.Move(Path.Combine(_tmp.Path, "old"), Path.Combine(_tmp.Path, "new"));

        Assert.Equal("案件B", Assert.Single(r.Failed).Name);
        Assert.True(File.Exists(locked));
        Assert.True(File.Exists(Path.Combine(_tmp.Path, @"old\案件B\case.db")));
        Assert.False(Directory.Exists(Path.Combine(_tmp.Path, @"new\案件B")));
    }

    [Fact]
    public void 有沒有資料_是否同一個位置()
    {
        Assert.False(DataRelocation.HasData(Path.Combine(_tmp.Path, "none")));
        Directory.CreateDirectory(Path.Combine(_tmp.Path, "empty"));
        Assert.False(DataRelocation.HasData(Path.Combine(_tmp.Path, "empty")));
        Write(@"some\x.txt", "x");
        Assert.True(DataRelocation.HasData(Path.Combine(_tmp.Path, "some")));
        Assert.True(DataRelocation.SameFolder(Path.Combine(_tmp.Path, "Some") + "\\", Path.Combine(_tmp.Path, "some")));
    }
}
