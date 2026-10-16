using LogTrace.Engine.Plants;
using LogTrace.Tests.Support;
using LogTrace.Workspace.Playback;

namespace LogTrace.Workspace.Tests;

// 廠別設定的快照與套用新版（#27）。測試資料：tests/fixtures/playback-prototype（虛構）
public sealed class PlantSnapshotViewModelTests : IDisposable
{
    readonly TempFolder _tmp = new();
    readonly string _plantPath;
    readonly MainViewModel _vm;

    public PlantSnapshotViewModelTests()
    {
        var plants = Directory.CreateDirectory(Path.Combine(_tmp.Path, "Plants")).FullName;
        _plantPath = Path.Combine(plants, "範例廠.json");
        File.Copy(Path.Combine(TempFolder.Fixture("playback-prototype"), "plant.json"), _plantPath);
        _vm = new MainViewModel(new SettingsStore(Path.Combine(_tmp.Path, "settings.json")), new FakeUserInteraction(), Path.Combine(_tmp.Path, "Cases"), _ => false);
    }

    public void Dispose()
    {
        _vm.Dispose();
        _tmp.Dispose();
    }

    async Task CreateCase()
    {
        _vm.RefreshPlants();
        _vm.SelectedPlant = _vm.Plants.Single(p => p.Path == _plantPath);
        _vm.NewCaseName = "回放";
        _vm.CreateCaseCommand.Execute(null);
        _vm.ImportSource = Path.Combine(TempFolder.Fixture("playback-prototype"), "logs");
        await _vm.ImportCommand.ExecuteAsync(null);
    }

    async Task Reopen()
    {
        var folder = _vm.CurrentCaseFolder!;
        _vm.CloseCaseCommand.Execute(null);
        _vm.SelectedCase = _vm.Cases.Single(c => c.Folder == folder);
        await _vm.OpenSelectedCaseCommand.ExecuteAsync(null);
    }

    void EditPlant(Action<PlantConfigDocument> edit)
    {
        var doc = PlantConfigFile.Load(_plantPath);
        edit(doc);
        PlantConfigFile.Save(doc, _plantPath);
    }

    [Fact]
    public async Task Layout來自案件的快照_廠別設定被刪除後仍能回放()
    {
        await CreateCase();
        File.Delete(_plantPath);

        await Reopen();

        Assert.Null(_vm.Playback.Message);
        Assert.Equal(11, _vm.Playback.Elements.Count);
        Assert.Null(_vm.NewerPlantNotice);
    }

    [Fact]
    public async Task 有新版時提示_選擇不套用後結果不變_同一修訂不再提示()
    {
        await CreateCase();
        Assert.Null(_vm.NewerPlantNotice);
        EditPlant(d => d.Layout.Elements.RemoveAll(e => e.Id is "eq-1" or "cv-3"));

        _vm.RefreshPlants(); // 回到主畫面時檢查（例如剛在 Layout 編輯器存檔）
        Assert.Contains("修訂 1 → 2", _vm.NewerPlantNotice);

        _vm.DismissNewerPlantCommand.Execute(null);
        Assert.Null(_vm.NewerPlantNotice);
        Assert.Contains(_vm.Playback.Elements, e => e.Id == "eq-1"); // 維持快照
        _vm.RefreshPlants();
        Assert.Null(_vm.NewerPlantNotice);

        EditPlant(_ => { }); // 有更新的修訂時再提示
        _vm.RefreshPlants();
        Assert.Contains("修訂 1 → 3", _vm.NewerPlantNotice);
    }

    [Fact]
    public async Task 開啟案件時有新版也會提示()
    {
        await CreateCase();
        EditPlant(_ => { });

        await Reopen();

        Assert.Contains("修訂 1 → 2", _vm.NewerPlantNotice);
    }

    [Fact]
    public async Task 套用新版後_Layout_解析欄位與狀態隨之更新()
    {
        await CreateCase();
        EditPlant(d =>
        {
            d.Layout.Elements.RemoveAll(e => e.Id is "eq-1" or "cv-3");
            d.ParseRules.RemoveAll(r => r.Effect is Engine.Parsing.EffectKinds.AlarmRaised);
        });
        _vm.RefreshPlants();

        await _vm.ApplyNewerPlantCommand.ExecuteAsync(null);

        Assert.Null(_vm.NewerPlantNotice);
        Assert.Equal("範例廠（虛構）（修訂 2）", _vm.CurrentCasePlant);
        Assert.DoesNotContain(_vm.Playback.Elements, e => e.Id == "eq-1");
        _vm.SelectedEvent = _vm.Events.Single(e => e.Event.Message.Contains("警報發生 A-02"));
        Assert.NotEqual(ElementLook.Alarm, _vm.Playback.Elements.Single(e => e.Id == "st-2").Look); // 沒有警報規則：推不出警報
        Assert.Contains("已套用廠別設定修訂 2", _vm.Status);
    }
}
