using LogTrace.Engine.Incidents;
using LogTrace.Tests.Support;

namespace LogTrace.Workspace.Tests;

// 異常紀錄面板的「已過時」（#167，ADR-0023 第 2 點）。測試資料：tests/fixtures/playback-prototype（虛構）
public sealed class IncidentObsoleteViewModelTests : IDisposable
{
    readonly TempFolder _tmp = new();
    readonly FakeUserInteraction _ui = new() { ConfirmResult = true };
    readonly MainViewModel _vm;
    readonly string _plants;

    public IncidentObsoleteViewModelTests()
    {
        _vm = new MainViewModel(new SettingsStore(Path.Combine(_tmp.Path, "settings.json")), _ui, Path.Combine(_tmp.Path, "Cases"), _ => false);
        _plants = Directory.CreateDirectory(Path.Combine(_tmp.Path, "Plants")).FullName;
        Inc.Clock = () => new DateTimeOffset(2030, 1, 11, 12, 0, 0, TimeSpan.FromHours(8));
        Inc.UserName = "甲";
    }

    public void Dispose()
    {
        _vm.Dispose();
        _tmp.Dispose();
    }

    Incidents.IncidentsViewModel Inc => _vm.Incidents;

    IncidentRecord OnDisk(string id) => new IncidentStore(Path.Combine(_plants, "範例廠.incidents")).LoadAll().Records.Single(r => r.Record.Id == id).Record;

    async Task<string> ReviewedRecord(string symptom = "搬送機停止（虛構）")
    {
        if (_vm.Events.Count == 0)
        {
            var fixture = TempFolder.Fixture("playback-prototype");
            File.Copy(Path.Combine(fixture, "plant.json"), Path.Combine(_plants, "範例廠.json"));
            _vm.RefreshPlants();
            _vm.SelectedPlant = _vm.Plants.Single(p => p.Path is not null);
            _vm.NewCaseName = "已過時";
            _vm.CreateCaseCommand.Execute(null);
            _vm.ImportSource = Path.Combine(fixture, "logs");
            await _vm.ImportCommand.ExecuteAsync(null);
        }
        _vm.SelectedEvent = _vm.Events.First(e => e.Event.Message.Contains("警報發生 SSCA01 E101"));
        Inc.NewFromCaseCommand.Execute(null);
        Inc.EditSymptom = symptom;
        Inc.EditCause = "行走馬達過載（虛構）";
        Inc.EditDisposal = "更換行走馬達（虛構）";
        Inc.EditResult = "復歸後正常（虛構）";
        Inc.EditExecutor = "工程師";
        Inc.UserName = "乙";
        Inc.ApproveCommand.Execute(null);
        Inc.UserName = "甲";
        return Inc.SelectedId!;
    }

    [Fact]
    public async Task 標示已過時_理由可以不填_查詢預設不列出_勾選顯示時列出並標示()
    {
        var id = await ReviewedRecord();
        Assert.True(Inc.MarkObsoleteCommand.CanExecute(null));

        Inc.MarkObsoleteCommand.Execute(null);

        Assert.Contains("標示為已過時", _ui.LastConfirmMessage);
        Assert.Contains("已把", Inc.Status);
        Assert.Equal(IncidentStatus.Obsolete, OnDisk(id).Status);
        Assert.Null(OnDisk(id).ObsoleteReason);
        Assert.Empty(Inc.Results);
        Inc.Reload();
        Assert.Contains("已過時 1 筆", Inc.Status);

        Inc.ShowObsolete = true;
        var row = Assert.Single(Inc.Results);
        Assert.Equal("已過時", row.Status);
        Assert.Equal("搬送機停止（虛構）", row.Symptom);
    }

    [Fact]
    public async Task 已過時的詳細資訊顯示理由與最後一版_可以恢復成草稿()
    {
        var id = await ReviewedRecord();
        Inc.ObsoleteReason = "設備已更換（虛構）";
        Inc.MarkObsoleteCommand.Execute(null);

        Assert.Contains("已過時（當時正確、現在不適用）：設備已更換（虛構）", Inc.Detail);
        Assert.Contains("最後一版：第 1 版", Inc.Detail);
        Assert.False(Inc.MarkObsoleteCommand.CanExecute(null));
        Assert.True(Inc.RestoreCommand.CanExecute(null));

        Inc.RestoreCommand.Execute(null);

        Assert.Equal(IncidentStatus.Draft, OnDisk(id).Status);
        Assert.Contains(Inc.Queue, q => q.Id == id && q.Kind == "恢復");
    }

    [Fact]
    public async Task 草稿不能標示已過時_取消確認時不標示()
    {
        var id = await ReviewedRecord();
        _ui.ConfirmResult = false;
        Inc.MarkObsoleteCommand.Execute(null);
        Assert.Equal(IncidentStatus.Reviewed, OnDisk(id).Status);

        _vm.SelectedEvent = _vm.Events.First(e => e.Event.Message.Contains("警報發生 SSCA01 E101"));
        Inc.NewFromCaseCommand.Execute(null);
        Assert.False(Inc.MarkObsoleteCommand.CanExecute(null));
    }
}
