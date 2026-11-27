using LogTrace.Engine.Cases;
using LogTrace.Engine.Events;
using LogTrace.Engine.Plants;
using LogTrace.Engine.Storage;
using LogTrace.Tests.Support;
using Microsoft.Data.Sqlite;

namespace LogTrace.Engine.Tests;

// 案件資料庫格式改變時以原始 LOG 重建（ADR-0030 第 6 點，#218）。
// 測試資料：tests/fixtures/multi-day（虛構）。舊格式以「把結構版本改成 2、清掉事件與搜尋用文字表」模擬。
public sealed class CaseUpgradeTests : IDisposable
{
    readonly TempFolder _tmp = new();
    readonly CaseLibrary _library;

    public CaseUpgradeTests() => _library = new CaseLibrary(Path.Combine(_tmp.Path, "Cases"));

    public void Dispose() => _tmp.Dispose();

    sealed record Snapshot(List<(StableEventId Id, long Ticks, string Raw)> Events, int Fields, TimeCorrection Correction, int Imports, List<string> Audit);

    (string Folder, Snapshot Before) CreateOldCase()
    {
        string folder;
        Snapshot before;
        using (var c = _library.Create("升級測試", PlantConfig.Default))
        {
            c.Import(TempFolder.Fixture("multi-day"));
            c.SetTimeCorrection(TimeCorrection.Create(TimeSpan.FromSeconds(3), []));
            folder = c.Folder;
            before = Take(c);
        }
        MakeOld(folder);
        return (folder, before);
    }

    static Snapshot Take(Case c)
    {
        var events = c.GetEvents(1, int.MaxValue);
        return new(events.Select(e => (e.Id, e.Timestamp.Ticks, e.RawTimestamp)).ToList(),
            events.Sum(e => c.GetFields(e.Sequence).Count), c.Info.Correction, c.GetImports().Count, c.GetAudit().Select(a => a.Action).ToList());
    }

    static void MakeOld(string folder)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(folder, Case.DatabaseFileName), Pooling = false }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE meta SET value = '2' WHERE key = 'schema_version'; DELETE FROM events; DELETE FROM event_fields; DROP TABLE event_search;";
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void 格式較舊的案件_列在清單中標示需要重建_直接開啟會被拒絕()
    {
        var (folder, _) = CreateOldCase();
        Assert.True(CaseUpgrade.NeedsRebuild(folder));
        var item = Assert.Single(_library.List());
        Assert.True(item.NeedsRebuild);
        Assert.Equal("升級測試", item.Name);
        Assert.Throws<InvalidDataException>(() => Case.Open(folder));
    }

    [Fact]
    public void 重建後_事件與使用者資料相同_稽核紀錄多一筆_備份在案件資料夾外()
    {
        var (folder, before) = CreateOldCase();
        var backup = CaseUpgrade.Rebuild(folder, _library.BackupsRoot);

        Assert.False(CaseUpgrade.NeedsRebuild(folder));
        Assert.False(Assert.Single(_library.List()).NeedsRebuild);
        using var c = Case.Open(folder);
        var after = Take(c);
        Assert.Equal(before.Events, after.Events); // 穩定識別碼、校正後時間、原始時間字串都相同
        Assert.Equal(before.Fields, after.Fields);
        Assert.Equal(before.Correction, after.Correction); // 時間偏移量保留
        Assert.Equal(before.Imports, after.Imports); // 匯入紀錄保留
        Assert.Equal(before.Audit.Concat(["重建案件資料庫"]), after.Audit);
        Assert.Equal("升級測試", c.Info.Name);

        Assert.Equal(Path.Combine(_tmp.Path, "Backups", Path.GetFileName(folder)), backup);
        Assert.False(backup.StartsWith(folder, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, SqliteEventStore.ReadSchemaVersion(Path.Combine(backup, Case.DatabaseFileName)));
        Assert.True(File.Exists(Path.Combine(backup, "說明.txt")));
        Assert.False(File.Exists(Path.Combine(folder, Case.DatabaseFileName + ".rebuilding")));
    }

    [Fact]
    public void 每個案件只保留最近一份備份()
    {
        var (folder, _) = CreateOldCase();
        CaseUpgrade.Rebuild(folder, _library.BackupsRoot);
        MakeOld(folder);
        var backup = CaseUpgrade.Rebuild(folder, _library.BackupsRoot);
        Assert.Single(Directory.GetDirectories(_library.BackupsRoot));
        Assert.Single(Directory.GetFiles(backup, "*.db"));
    }

    [Fact]
    public void 重建失敗_case_db不變_仍需要重建_沒有留下暫存檔()
    {
        var (folder, _) = CreateOldCase();
        var db = Path.Combine(folder, Case.DatabaseFileName);
        var original = File.ReadAllBytes(db);
        // 原始 LOG 不見了：無法重建
        foreach (var f in Directory.EnumerateFiles(Path.Combine(folder, Case.RawFolderName), "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(f, FileAttributes.Normal);
            File.Delete(f);
        }
        Assert.ThrowsAny<Exception>(() => CaseUpgrade.Rebuild(folder, _library.BackupsRoot));
        Assert.Equal(original, File.ReadAllBytes(db));
        Assert.True(CaseUpgrade.NeedsRebuild(folder));
        Assert.False(File.Exists(db + ".rebuilding"));
    }

    [Fact]
    public void 取消_case_db不變()
    {
        var (folder, _) = CreateOldCase();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => CaseUpgrade.Rebuild(folder, _library.BackupsRoot, cts.Token));
        Assert.True(CaseUpgrade.NeedsRebuild(folder));
        Assert.False(File.Exists(Path.Combine(folder, Case.DatabaseFileName + ".rebuilding")));
    }
}
