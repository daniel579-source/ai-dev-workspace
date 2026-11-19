using LogTrace.Engine.Flows;
using LogTrace.Tests.Support;

namespace LogTrace.Workspace.Tests;

// 正常流程樣板與卡住比對的主畫面（ADR-0024，#192）。LOG、編號與時間全部是虛構的（tests/fixtures/stall）。
public sealed class NormalFlowViewModelTests
{
    static async Task<(MainViewModel Vm, FakeUserInteraction Ui, string Plants)> ImportStallCase(TempFolder tmp)
    {
        var ui = new FakeUserInteraction();
        var vm = new MainViewModel(new SettingsStore(Path.Combine(tmp.Path, "settings.json")), ui, Path.Combine(tmp.Path, "Cases"), _ => false);
        var plants = Directory.CreateDirectory(Path.Combine(tmp.Path, "Plants")).FullName;
        File.Copy(Path.Combine(TempFolder.Fixture("playback-prototype"), "plant.json"), Path.Combine(plants, "範例廠.json"));
        vm.RefreshPlants();
        vm.SelectedPlant = vm.Plants.Single(p => p.Path is not null);
        vm.NewCaseName = "卡住";
        vm.CreateCaseCommand.Execute(null);
        vm.ImportSource = TempFolder.Fixture("stall");
        await vm.ImportCommand.ExecuteAsync(null);
        return (vm, ui, plants);
    }

    [Fact]
    public async Task 匯入後建立樣板檔_收集進度顯示位置_卡住的事件顯示還不能判斷與進度()
    {
        using var tmp = new TempFolder();
        var (vm, _, plants) = await ImportStallCase(tmp);
        using var _ = vm;

        var path = Path.Combine(plants, "範例廠.flows.json");
        Assert.True(File.Exists(path));
        Assert.Contains(path, vm.FlowStatus);

        vm.SeekTime(new DateTime(2030, 1, 12, 9, 6, 30)); // 第 1 段抵達後卡住的時段
        Assert.Contains("正常流程比對（命令 00050001，還不能判斷）", vm.SelectedDetail);
        Assert.Contains("樣本 0／5", vm.SelectedDetail);
        Assert.Contains("出處：事件 #", vm.SelectedDetail);
    }

    [Fact]
    public async Task 匯出後再匯入同一份_顯示重複並確認後合併_別的廠拒絕()
    {
        using var tmp = new TempFolder();
        var (vm, ui, plants) = await ImportStallCase(tmp);
        using var _ = vm;
        var path = Path.Combine(plants, "範例廠.flows.json");
        var (file, stamp) = FlowStore.Load(path);
        // 虛構樣本：讓匯出有內容
        file = file! with { Samples = [new FlowSample { SourceHash = "H1", Command = "C-1", Segment = 1, Carrier = "SSCA01", From = "A-01", To = "1003", CollectedAt = DateTimeOffset.Now }] };
        FlowStore.Save(path, file, stamp);
        vm.ReloadKnowledgeCommand.Execute(null);

        ui.FlowSavePathResult = Path.Combine(tmp.Path, "匯出.flows.json");
        vm.ExportFlowsCommand.Execute(null);
        Assert.True(File.Exists(ui.FlowSavePathResult));

        ui.FlowFileResult = ui.FlowSavePathResult;
        ui.ConfirmResult = true;
        vm.ImportFlowsCommand.Execute(null);
        Assert.Contains("新增 0 筆，重複 1 筆", ui.LastConfirmMessage);
        Assert.Contains("已匯入正常流程樣板", vm.Status);
        Assert.Single(FlowStore.Load(path).File!.Samples);

        FlowStore.Export(ui.FlowFileResult, file with { Plant = "別的廠" });
        vm.ImportFlowsCommand.Execute(null);
        Assert.Contains("不能匯入", vm.Status);
    }
}
