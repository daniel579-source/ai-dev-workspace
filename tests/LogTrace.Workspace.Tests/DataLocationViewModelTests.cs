using LogTrace.Engine.Cases;
using LogTrace.Engine.Plants;
using LogTrace.Tests.Support;

namespace LogTrace.Workspace.Tests;

// 預設存放位置改為 D 槽、舊位置的資料先詢問再搬移（#57）。這裡以暫存資料夾代替 D 槽與 %LOCALAPPDATA%；內容全部是虛構的。
public sealed class DataLocationViewModelTests : IDisposable
{
    readonly TempFolder _tmp = new();
    readonly List<MainViewModel> _vms = [];

    string Old(string name) => Path.Combine(_tmp.Path, "old", name);
    string New(string name) => Path.Combine(_tmp.Path, "new", name);
    string SettingsPath => Path.Combine(_tmp.Path, "settings.json");

    public void Dispose()
    {
        foreach (var vm in _vms) vm.Dispose();
        _tmp.Dispose();
    }

    MainViewModel NewViewModel(FakeUserInteraction ui)
    {
        var vm = new MainViewModel(new SettingsStore(SettingsPath), ui, New("Cases"), _ => false, New("Plants"));
        _vms.Add(vm);
        return vm;
    }

    void OldData()
    {
        new CaseLibrary(Old("Cases")).Create("舊案件", PlantConfig.Default).Dispose();
        PlantConfigFile.Save(new PlantConfigDocument { Plant = "範例廠" }, Path.Combine(Old("Plants"), "範例廠.json"));
    }

    [Fact]
    public void 舊位置有資料_同意後搬到新位置_清單與位置都更新()
    {
        OldData();
        var ui = new FakeUserInteraction { ConfirmResult = true };
        var vm = NewViewModel(ui);

        vm.OfferRelocation(Old("Cases"), Old("Plants"));

        Assert.Contains("搬到新位置嗎", ui.LastConfirmMessage);
        Assert.Contains(Old("Cases"), ui.LastConfirmMessage);
        Assert.Equal(["舊案件"], vm.Cases.Select(c => c.Name));
        Assert.Equal(New("Cases"), vm.CasesRoot);
        Assert.True(File.Exists(Path.Combine(New("Plants"), "範例廠.json")));
        Assert.Contains(vm.Plants, p => p.Path == Path.Combine(New("Plants"), "範例廠.json"));
        Assert.False(Directory.Exists(Old("Cases")));
        Assert.False(Directory.Exists(Old("Plants")));
        Assert.Contains("案件：搬移 2 項", vm.Status); // 案件資料夾與稽核紀錄
    }

    [Fact]
    public void 不同意時繼續使用舊位置_之後不再詢問()
    {
        OldData();
        var vm = NewViewModel(new FakeUserInteraction { ConfirmResult = false });

        vm.OfferRelocation(Old("Cases"), Old("Plants"));

        Assert.Equal(Old("Cases"), vm.CasesRoot);
        Assert.Equal(Old("Plants"), vm.PlantsRoot);
        Assert.Equal(["舊案件"], vm.Cases.Select(c => c.Name));
        Assert.True(Directory.Exists(Old("Cases")));

        var ui = new FakeUserInteraction { ConfirmResult = true };
        var reopened = NewViewModel(ui);
        reopened.OfferRelocation(Old("Cases"), Old("Plants"));
        Assert.Null(ui.LastConfirmMessage);
        Assert.Equal(Old("Cases"), reopened.CasesRoot);
    }

    [Fact]
    public void 舊位置沒有資料時不詢問_位置維持新的預設()
    {
        var ui = new FakeUserInteraction { ConfirmResult = true };
        var vm = NewViewModel(ui);

        vm.OfferRelocation(Old("Cases"), Old("Plants"));

        Assert.Null(ui.LastConfirmMessage);
        Assert.Equal(New("Cases"), vm.CasesRoot);
        Assert.Equal(New("Plants"), vm.PlantsRoot);
        Assert.Null(vm.PlantsRootWarning);
    }

    [Fact]
    public void 廠別設定位置可以變更_重新開啟後保留()
    {
        var other = Path.Combine(_tmp.Path, "其他位置");
        var vm = NewViewModel(new FakeUserInteraction { FolderResult = other });

        vm.ChangePlantsRootCommand.Execute(null);

        Assert.Equal(other, vm.PlantsRoot);
        Assert.Equal(other, NewViewModel(new FakeUserInteraction()).PlantsRoot);
    }

    [Fact]
    public void 存放位置所在的磁碟不存在時_明確提示()
    {
        var missing = "QRSTUVWXYZ".Select(c => $"{c}:\\").First(d => !Directory.Exists(d));
        var vm = new MainViewModel(new SettingsStore(SettingsPath), new FakeUserInteraction(), Path.Combine(missing, "LogTrace", "Cases"), _ => false,
            Path.Combine(missing, "LogTrace", "Plants"));
        _vms.Add(vm);

        Assert.Contains($"（{missing.TrimEnd('\\')}）不存在", vm.CasesRootWarning);
        Assert.Contains("廠別設定存放位置", vm.PlantsRootWarning);
    }
}
