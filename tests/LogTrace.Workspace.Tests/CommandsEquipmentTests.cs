using LogTrace.Tests.Support;
using LogTrace.Workspace.Playback;

namespace LogTrace.Workspace.Tests;

// 命令清單、設備狀態表、未對應清單（#26）。測試資料：tests/fixtures/playback-prototype（虛構，案件從 10:00:00 起）
public sealed class CommandsEquipmentTests : IDisposable
{
    static readonly DateTime T0 = new(2030, 1, 11, 10, 0, 0);

    readonly TempFolder _tmp = new();
    readonly MainViewModel _vm;

    public CommandsEquipmentTests() =>
        _vm = new MainViewModel(new SettingsStore(Path.Combine(_tmp.Path, "settings.json")), new FakeUserInteraction(), Path.Combine(_tmp.Path, "Cases"), _ => false);

    public void Dispose()
    {
        _vm.Dispose();
        _tmp.Dispose();
    }

    async Task Open()
    {
        var plants = Directory.CreateDirectory(Path.Combine(_tmp.Path, "Plants")).FullName;
        File.Copy(Path.Combine(TempFolder.Fixture("playback-prototype"), "plant.json"), Path.Combine(plants, "範例廠.json"));
        _vm.RefreshPlants();
        _vm.SelectedPlant = _vm.Plants.Single(p => p.Path is not null);
        _vm.NewCaseName = "命令";
        _vm.CreateCaseCommand.Execute(null);
        _vm.ImportSource = Path.Combine(TempFolder.Fixture("playback-prototype"), "logs");
        await _vm.ImportCommand.ExecuteAsync(null);
    }

    static DateTime At(double s) => T0.AddSeconds(s);

    EquipmentRowViewModel Equip(string name) => _vm.Playback.Equipment.Single(e => e.Name == name);

    [Fact]
    public async Task 命令清單_事件數與案件結束時的狀態()
    {
        await Open();

        Assert.Equal(["00040001", "00040002", "00040003"], _vm.Commands.Select(c => c.Id));
        Assert.Equal([6L, 6, 2], _vm.Commands.Select(c => c.EventCount));
        Assert.Equal("已完成", _vm.Commands[0].FinalStatus);
        Assert.Equal("已完成（已交出）", _vm.Commands[1].FinalStatus);
        Assert.Equal("案件範圍內未完成（第 1 段執行中（SSCA01 → 1003））", _vm.Commands[2].FinalStatus);
    }

    [Fact]
    public async Task 選一個命令_列出串在命令上的所有事件_與人工比對一致_點一筆跳到該時刻()
    {
        await Open();

        _vm.SelectedCommand = _vm.Commands[0];

        // 人工列出（虛構資料）：建立、派送第 1 段、抵達 1003、派送第 2 段、抵達 0100302、完成回報
        Assert.Equal(
        [
            "建立任務 00040001 類型=S 棧板=PLT-EX-0001 來源=A-01 目的地=0100302",
            "派送 00040001 第1段 → SSCA01 目的地 1003",
            "00040001 第1段 抵達 1003（SSCA01）",
            "派送 00040001 第2段 → SRCA01 目的地 0100302",
            "00040001 第2段 抵達 0100302（SRCA01）",
            "完成回報 00040001",
        ], _vm.CommandEvents.Select(e => e.Event.Message));
        Assert.Equal(["EXE", "Server", "Server", "Server", "Server", "EXE"], _vm.CommandEvents.Select(e => e.Module)); // 跨模組

        _vm.GoToCommandEventCommand.Execute(_vm.CommandEvents[3]);
        Assert.Equal(At(45), _vm.CurrentTime);
        Assert.Same(_vm.CommandEvents[3], _vm.SelectedEvent);
    }

    [Fact]
    public async Task 命令的目前狀態隨目前時刻更新()
    {
        await Open();

        _vm.SeekTime(At(50));
        Assert.Equal("第 2 段執行中（SRCA01 → 0100302）", _vm.Commands[0].CurrentStatus);
        Assert.Equal("—", _vm.Commands[1].CurrentStatus); // 還沒建立

        _vm.SeekTime(At(100));
        Assert.Equal("已完成", _vm.Commands[0].CurrentStatus);
    }

    [Fact]
    public async Task 設備狀態表隨目前時刻更新_與Layout面板的狀態一致()
    {
        await Open();

        _vm.SeekTime(At(0));
        Assert.Equal(7, _vm.Playback.Equipment.Count); // 站點 4、搬送機 2、對接設備 1
        Assert.Equal("在席 未知", Equip("入庫口1").State);
        Assert.Equal("連線 未知；模式 未知；作業未知", Equip("SRCA01").State);

        _vm.SeekTime(At(170));
        Assert.Contains("在席 OFF；貨物 PLT-EX-0002；不一致", Equip("出庫站4").State);
        Assert.True(Equip("出庫站4").IsAbnormal);
        Assert.Equal("連線 ON-LINE；模式 自動；閒置", Equip("SRCA01").State);
        foreach (var row in _vm.Playback.Equipment)
            Assert.Equal(_vm.Playback.Elements.Single(e => e.Id == row.Id).Look, row.Look); // 與 Layout 面板相同

        _vm.SeekTime(At(200));
        Assert.StartsWith("異常中（警報 E205，未見復歸紀錄）", Equip("輸送站2").State);
        Assert.StartsWith("異常中（警報 E101）", Equip("SSCA01").State);
    }

    [Fact]
    public async Task 只看異常中或不一致的設備()
    {
        await Open();
        _vm.SeekTime(At(200));

        _vm.Playback.OnlyAbnormal = true;

        // 200 秒：輸送站2 與 SSCA01 有警報；出庫站4 的棧板已在 175 秒被 SSCA01 取走，不再不一致
        Assert.Equal(["SSCA01", "輸送站2"], _vm.Playback.Equipment.Select(e => e.Name).Order(StringComparer.Ordinal));
        _vm.SeekTime(At(170));
        Assert.Equal(["出庫站4"], _vm.Playback.Equipment.Select(e => e.Name)); // 不一致也算
        _vm.SeekTime(At(240)); // SSCA01 的警報已復歸；輸送站2 沒有復歸
        Assert.Equal(["輸送站2"], _vm.Playback.Equipment.Select(e => e.Name));
        Assert.DoesNotContain(_vm.Playback.Equipment, e => e.Name == "SSCA01");

        _vm.Playback.OnlyAbnormal = false;
        Assert.Equal(7, _vm.Playback.Equipment.Count);
    }

    [Fact]
    public async Task 設備狀態表可以依欄位排序_再按一次反向()
    {
        await Open();
        _vm.SeekTime(At(200));

        Assert.Equal(_vm.Playback.Equipment.Select(e => e.Name).Order(StringComparer.CurrentCulture), _vm.Playback.Equipment.Select(e => e.Name)); // 預設依名稱

        _vm.Playback.SortEquipmentCommand.Execute(EquipmentSort.Abnormal);
        Assert.All(_vm.Playback.Equipment.Take(2), e => Assert.True(e.IsAbnormal));
        Assert.All(_vm.Playback.Equipment.Skip(2), e => Assert.False(e.IsAbnormal));

        _vm.Playback.SortEquipmentCommand.Execute(EquipmentSort.Abnormal);
        Assert.True(_vm.Playback.SortDescending);
        Assert.All(_vm.Playback.Equipment.Take(5), e => Assert.False(e.IsAbnormal));

        _vm.Playback.SortEquipmentCommand.Execute(EquipmentSort.Type);
        Assert.False(_vm.Playback.SortDescending);
        Assert.Equal(_vm.Playback.Equipment.Select(e => e.TypeLabel).Order(StringComparer.CurrentCulture), _vm.Playback.Equipment.Select(e => e.TypeLabel));
    }

    [Fact]
    public async Task 未對應清單與目前時刻同步()
    {
        await Open();

        _vm.SeekTime(At(100)); // 前後 30 秒（70～130 秒）：A-09 心跳在 88.3 與 117.3 秒
        var a09 = Assert.Single(_vm.UnmappedNearby);
        Assert.Equal(("站點", "A-09", 2L, 12L), (a09.KindLabel, a09.Value, a09.NearbyCount, a09.TotalCount));

        _vm.SeekTime(At(360)); // 結尾附近：SSCA03 只在 360 秒出現
        Assert.Contains(_vm.UnmappedNearby, u => u.Value == "SSCA03" && u.NearbyCount == 1 && u.TotalCount == 1 && u.KindLabel == "搬送機");
        Assert.DoesNotContain(_vm.UnmappedNearby, u => u.Value is "A-01" or "SSCA01"); // 已對應的編號不列出
    }
}
