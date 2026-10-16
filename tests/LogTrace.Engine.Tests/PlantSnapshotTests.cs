using LogTrace.Engine.Cases;
using LogTrace.Engine.Parsing;
using LogTrace.Engine.Playback;
using LogTrace.Engine.Plants;
using LogTrace.Tests.Support;

namespace LogTrace.Engine.Tests;

// 廠別設定的快照與套用新版（ADR-0006 第 8 點）。測試資料：tests/fixtures/playback-prototype（虛構）
public sealed class PlantSnapshotTests : IDisposable
{
    readonly TempFolder _tmp = new();
    readonly CaseLibrary _cases;
    readonly PlantConfigLibrary _plants;
    readonly string _plantPath;

    public PlantSnapshotTests()
    {
        _cases = new CaseLibrary(Path.Combine(_tmp.Path, "Cases"));
        _plants = new PlantConfigLibrary(Path.Combine(_tmp.Path, "Plants"));
        Directory.CreateDirectory(_plants.Root);
        _plantPath = Path.Combine(_plants.Root, "範例廠.json");
        File.Copy(Path.Combine(TempFolder.Fixture("playback-prototype"), "plant.json"), _plantPath);
    }

    public void Dispose() => _tmp.Dispose();

    static string Logs => Path.Combine(TempFolder.Fixture("playback-prototype"), "logs");

    /// <summary>以存放位置的廠別設定建立案件並匯入（第 1 版）。</summary>
    Case CreateCase(PlantConfigDocument? doc = null)
    {
        doc ??= PlantConfigFile.Load(_plantPath);
        var c = _cases.Create("回放", doc.ToPlantConfig(), doc, _plantPath);
        c.Import(Logs);
        return c;
    }

    /// <summary>修改廠別設定檔並存檔（修訂號加 1）。</summary>
    PlantConfigDocument EditPlant(Action<PlantConfigDocument> edit)
    {
        var doc = PlantConfigFile.Load(_plantPath);
        edit(doc);
        PlantConfigFile.Save(doc, _plantPath);
        return doc;
    }

    static WorldModel World(Case c) =>
        new(c.GetEffects(), c.PlantSnapshot!.Layout, c.Info.Plant.PresenceSignal);

    [Fact]
    public void 案件保存廠別設定的快照_含Layout_重新開啟後相同()
    {
        string folder;
        using (var c = CreateCase()) folder = c.Folder;

        using var opened = Case.Open(folder);

        Assert.Equal(PlantConfigFile.Serialize(PlantConfigFile.Load(_plantPath)), PlantConfigFile.Serialize(opened.PlantSnapshot!));
        Assert.Equal(11, opened.PlantSnapshot!.Layout.Elements.Count);
        Assert.Equal(_plantPath, opened.PlantSnapshotSource);
    }

    [Fact]
    public void 廠別設定被修改後_舊案件仍以快照開啟()
    {
        string folder;
        using (var c = CreateCase()) folder = c.Folder;
        EditPlant(d => d.Layout.Elements.RemoveAll(e => e.Type is LayoutElementType.Station or LayoutElementType.Conveyor));

        using var opened = Case.Open(folder);

        Assert.Equal(1, opened.Info.Plant.Revision);
        Assert.Equal(4, opened.PlantSnapshot!.Layout.Elements.Count(e => e.Type == LayoutElementType.Station));
        Assert.True(World(opened).Final.Stations.ContainsKey("st-1"));
    }

    [Fact]
    public void 廠別設定被刪除後_舊案件仍以快照開啟_沒有新版()
    {
        string folder;
        using (var c = CreateCase()) folder = c.Folder;
        File.Delete(_plantPath);

        using var opened = Case.Open(folder);

        Assert.NotNull(opened.PlantSnapshot);
        Assert.Null(opened.FindNewerPlant(_plants));
        Assert.True(World(opened).Final.Alarms.ContainsKey("st-2"));
    }

    [Fact]
    public void 有新版時找得到_沒有新版時為null()
    {
        using var c = CreateCase();
        Assert.Null(c.FindNewerPlant(_plants));

        EditPlant(_ => { });

        var newer = c.FindNewerPlant(_plants);
        Assert.NotNull(newer);
        Assert.Equal((2, _plantPath), (newer.Value.Document.Revision, newer.Value.Path));
    }

    [Fact]
    public void 設定檔移到別的檔名_依廠別名稱仍找得到新版()
    {
        using var c = CreateCase();
        EditPlant(_ => { });
        File.Move(_plantPath, Path.Combine(_plants.Root, "改名.json"));

        Assert.EndsWith("改名.json", c.FindNewerPlant(_plants)!.Value.Path);
    }

    [Fact]
    public void 套用新版_解析欄位與狀態隨之更新_寫入稽核紀錄()
    {
        // 第 1 版沒有警報的規則：推不出警報
        var v1 = PlantConfigFile.Load(_plantPath);
        v1.ParseRules.RemoveAll(r => r.Effect is EffectKinds.AlarmRaised or EffectKinds.AlarmCleared);
        PlantConfigFile.Save(v1, _plantPath);
        using var c = CreateCase(PlantConfigFile.Load(_plantPath));
        var before = c.GetEvents(1, int.MaxValue).ToList();
        Assert.Empty(World(c).Final.Alarms);

        var v3 = EditPlant(d => d.ParseRules = [.. PlantConfigFile.Load(Path.Combine(TempFolder.Fixture("playback-prototype"), "plant.json")).ParseRules]);
        var newer = c.FindNewerPlant(_plants)!.Value;
        c.ApplyPlant(newer.Document, newer.Path);

        Assert.Equal(3, c.Info.Plant.Revision);
        Assert.True(World(c).Final.Alarms.ContainsKey("st-2"));
        Assert.Contains(c.GetIdentifierCounts(), x => x.Value == "A-02");
        Assert.Equal(before.Select(e => e.Id), c.GetEvents(1, int.MaxValue).Select(e => e.Id)); // 穩定識別碼不變
        var audit = c.GetAudit()[^1];
        Assert.Equal(("套用新版廠別設定", "範例廠（虛構）"), (audit.Action, audit.Target));
        Assert.StartsWith("修訂 2 → 3", audit.Detail);
        Assert.Null(c.FindNewerPlant(_plants));

        using var reopened = Case.Open(c.Folder);
        Assert.Equal(3, reopened.PlantSnapshot!.Revision);
        Assert.Equal(v3.ParseRules.Count, reopened.Info.Plant.ParseRules.Count);
    }

    [Fact]
    public void 套用新版_模組順序與編碼改變時_重新排序()
    {
        using var c = CreateCase();
        var firstBefore = c.GetEvents(1, 3).Select(e => e.Module).ToList();

        EditPlant(d => d.ModuleOrder = ["Server", "PLC", "EXE"]);
        c.ApplyPlant(c.FindNewerPlant(_plants)!.Value.Document, _plantPath);

        // 10:00:10 同時間戳記：PLC（取物可）與 Server（派送）的先後依新的模組順序
        var at10 = c.GetEvents(1, int.MaxValue).Where(e => e.Timestamp == new DateTime(2030, 1, 11, 10, 0, 10)).Select(e => e.Module).ToList();
        Assert.Equal(["Server", "PLC"], at10);
        Assert.NotEmpty(firstBefore);
    }

    [Fact]
    public void 不套用時_結果維持快照()
    {
        using var c = CreateCase();
        var before = c.GetEvents(1, int.MaxValue).ToList();

        EditPlant(d => d.ParseRules.Clear());

        Assert.NotNull(c.FindNewerPlant(_plants));
        Assert.Equal(before, c.GetEvents(1, int.MaxValue));
        Assert.Equal(1, c.Info.Plant.Revision);
        Assert.NotEmpty(c.GetEffects());
    }

    [Fact]
    public void 快照功能加入前建立的案件_依廠別名稱找到新版並套用()
    {
        var doc = PlantConfigFile.Load(_plantPath);
        using var c = _cases.Create("舊案件", doc.ToPlantConfig()); // 沒有快照
        c.Import(Logs);
        Assert.Null(c.PlantSnapshot);

        EditPlant(_ => { });
        var newer = c.FindNewerPlant(_plants)!.Value;
        c.ApplyPlant(newer.Document, newer.Path);

        Assert.NotNull(c.PlantSnapshot);
        Assert.Equal(2, c.Info.Plant.Revision);
    }

    [Fact]
    public void 內建預設的案件沒有新版()
    {
        using var c = _cases.Create("預設", PlantConfig.Default);

        Assert.Null(c.FindNewerPlant(_plants));
    }
}
