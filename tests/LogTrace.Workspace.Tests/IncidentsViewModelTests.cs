using LogTrace.Engine.Incidents;
using LogTrace.Tests.Support;

namespace LogTrace.Workspace.Tests;

// 異常紀錄面板（#110，ADR-0016）。測試資料：tests/fixtures/playback-prototype 的 plant.json 與 LOG（虛構）
public sealed class IncidentsViewModelTests : IDisposable
{
    readonly TempFolder _tmp = new();
    readonly FakeUserInteraction _ui = new() { ConfirmResult = true };
    readonly MainViewModel _vm;
    readonly string _plants;
    DateTimeOffset _now = new(2030, 1, 11, 12, 0, 0, TimeSpan.FromHours(8));

    public IncidentsViewModelTests()
    {
        _vm = new MainViewModel(new SettingsStore(Path.Combine(_tmp.Path, "settings.json")), _ui, Path.Combine(_tmp.Path, "Cases"), _ => false);
        _plants = Directory.CreateDirectory(Path.Combine(_tmp.Path, "Plants")).FullName;
        Inc.Clock = () => _now;
        Inc.UserName = "甲";
    }

    public void Dispose()
    {
        _vm.Dispose();
        _tmp.Dispose();
    }

    Incidents.IncidentsViewModel Inc => _vm.Incidents;

    string Folder => Path.Combine(_plants, "範例廠.incidents");

    async Task OpenCase(string name = "虛構案件", bool withPlant = true, string logs = "playback-prototype/logs")
    {
        var fixture = TempFolder.Fixture("playback-prototype");
        if (withPlant && !File.Exists(Path.Combine(_plants, "範例廠.json"))) File.Copy(Path.Combine(fixture, "plant.json"), Path.Combine(_plants, "範例廠.json"));
        _vm.RefreshPlants();
        _vm.SelectedPlant = withPlant ? _vm.Plants.Single(p => p.Path is not null) : _vm.Plants.Single(p => p.Path is null);
        _vm.NewCaseName = name;
        _vm.CreateCaseCommand.Execute(null);
        _vm.ImportSource = Path.Combine(TempFolder.Fixture(""), logs);
        await _vm.ImportCommand.ExecuteAsync(null);
    }

    void Select(string text) => _vm.SelectedEvent = _vm.Events.First(e => e.Event.Message.Contains(text));

    void FillRequired()
    {
        Inc.EditSymptom = "搬送機停止（虛構）";
        Inc.EditCause = "行走馬達過載（虛構）";
        Inc.EditDisposal = "檢查行走馬達（虛構）";
        Inc.EditResult = "復歸後正常（虛構）";
        Inc.EditExecutor = "工程師";
    }

    IncidentRecord OnDisk(string id) => new IncidentStore(Folder).LoadAll().Records.Single(r => r.Record.Id == id).Record;

    [Fact]
    public async Task 開啟案件時讀取廠別旁邊的資料夾_沒有紀錄時是空的()
    {
        await OpenCase();

        Assert.Empty(Inc.Results);
        Assert.Empty(Inc.Queue);
        Assert.Contains("範例廠.incidents", Inc.Status);
        Assert.True(Inc.NewFromCaseCommand.CanExecute(null));
    }

    [Fact]
    public async Task 找不到廠別設定檔時不能使用()
    {
        await OpenCase(withPlant: false);

        Assert.Contains("找不到廠別", Inc.Status);
        Assert.False(Inc.NewFromCaseCommand.CanExecute(null));
    }

    [Fact]
    public async Task 從案件建立_帶入選取的事件_遮罩後的訊息與警報代碼設備()
    {
        await OpenCase();
        Select("警報發生 SSCA01 E101");

        Inc.NewFromCaseCommand.Execute(null);

        var id = Assert.Single(Inc.Queue).Id;
        Assert.Equal("新草稿", Inc.Queue[0].Kind);
        Assert.True(Inc.IsEditing);
        var ev = Assert.Single(Inc.RecordEvents);
        Assert.Equal("警報發生 SSCA01 E101", ev.Message.Trim());
        Assert.Contains("E101", Inc.EditAlarmCodes);
        Assert.Contains("SSCA01", Inc.EditDevices);
        Assert.Equal("虛構案件", Inc.EditCaseName);
        Assert.Equal("範例廠（虛構）", Inc.EditPlant);
        Assert.Equal(ev.Time, Inc.EditFrom);
        Assert.Equal("甲", OnDisk(id).Author);
        Assert.True(File.Exists(Path.Combine(Folder, id + ".json")));
        Assert.Empty(Inc.Results); // 草稿不列在查詢中
    }

    [Fact]
    public async Task 加入事件時訊息一律遮罩()
    {
        await OpenCase(logs: "search-cases");
        _vm.SelectedEvent = null;
        Inc.NewFromCaseCommand.Execute(null);
        Select("簡訊通知");
        _vm.RevealSensitive = true; // 畫面上顯示敏感資訊時，保存的內容仍然遮罩

        Inc.AddSelectedEventCommand.Execute(null);

        var ev = Assert.Single(Inc.RecordEvents);
        Assert.Contains("簡訊通知 09xx-xxx-xxx", ev.Message);
        Assert.DoesNotContain("345", ev.Message);
        Assert.Contains("按「存檔」後保存", Inc.Status);
    }

    [Fact]
    public async Task 擋下時不能審核_補齊後審核通過_查詢列出()
    {
        await OpenCase();
        Select("警報發生 SSCA01 E101");
        Inc.NewFromCaseCommand.Execute(null);
        var id = Inc.SelectedId!;

        Inc.ApproveCommand.Execute(null);
        Assert.Contains("不能審核通過", Inc.Status);
        Assert.Contains("「現象」空白", Inc.CheckText);
        Assert.Equal(IncidentStatus.Draft, OnDisk(id).Status);

        FillRequired();
        Inc.UserName = "乙";
        Assert.Equal("可以審核通過", Inc.CheckText);
        Inc.ApproveCommand.Execute(null);

        Assert.Equal(IncidentStatus.Reviewed, OnDisk(id).Status);
        Assert.Equal("乙", OnDisk(id).Versions[0].Reviewer);
        Assert.Empty(Inc.Queue);
        Assert.Single(Inc.Results);
        Assert.False(Inc.IsEditing);
    }

    [Fact]
    public async Task 撰寫者與審核者同一人只提醒_確認後通過_取消時不通過()
    {
        await OpenCase();
        Select("警報發生 SSCA01 E101");
        Inc.NewFromCaseCommand.Execute(null);
        var id = Inc.SelectedId!;
        FillRequired();
        Assert.Contains("提醒：撰寫者與審核者同一人", Inc.CheckText);

        _ui.ConfirmResult = false;
        Inc.ApproveCommand.Execute(null);
        Assert.Equal(IncidentStatus.Draft, OnDisk(id).Status);
        Assert.Contains("撰寫者與審核者同一人", _ui.LastConfirmMessage);

        _ui.ConfirmResult = true;
        Inc.ApproveCommand.Execute(null);
        Assert.Equal(["撰寫者與審核者同一人"], OnDisk(id).Versions[0].Warnings);
    }

    [Fact]
    public async Task AI推測的勾選_取消勾選才能審核()
    {
        await OpenCase();
        Select("警報發生 SSCA01 E101");
        Inc.NewFromCaseCommand.Execute(null);
        FillRequired();
        Inc.AiCause = true;
        Inc.SaveCommand.Execute(null);

        Assert.Equal([IncidentFields.Cause], OnDisk(Inc.SelectedId!).Work!.AiGuess);
        Assert.Contains("「原因」仍是 AI 推測", Inc.CheckText);
        Assert.Contains("「原因」仍是 AI 推測", Inc.Queue[0].Blocks);
        Assert.False(Inc.Queue[0].CanApprove);

        Inc.AiCause = false;
        Assert.DoesNotContain("AI 推測", Inc.CheckText);
    }

    async Task<string> ReviewedRecord()
    {
        await OpenCase();
        Select("警報發生 SSCA01 E101");
        Inc.NewFromCaseCommand.Execute(null);
        FillRequired();
        Inc.UserName = "乙";
        Inc.ApproveCommand.Execute(null);
        Inc.UserName = "甲";
        return Inc.SelectedId!;
    }

    [Fact]
    public async Task 修訂中舊版仍有效_詳細資訊標示尚未審核的變更_審核後標示改過的欄位()
    {
        var id = await ReviewedRecord();

        Inc.ReviseCommand.Execute(null);
        Inc.EditCause = "減速機異常（虛構）";
        Inc.SaveCommand.Execute(null);

        Assert.Equal("修訂中（舊版仍有效）", Assert.Single(Inc.Results).Status);
        Assert.Equal("修訂中", Assert.Single(Inc.Queue).Kind);
        Assert.Contains("　 原因：行走馬達過載（虛構）", Inc.Detail); // 有效的仍是舊版
        Assert.Contains(Inc.DetailLines, l => l.Pending && l.Text.Contains("原因：行走馬達過載（虛構） → 減速機異常（虛構）"));

        Inc.UserName = "乙";
        Inc.ApproveCommand.Execute(null);

        Assert.Equal(2, OnDisk(id).Versions.Count);
        var row = Assert.Single(Inc.Results);
        Assert.Equal("★ 第 2 版改過：原因", row.Changed);
        Assert.Contains(Inc.DetailLines, l => l.Changed && l.Text == "★ 原因：減速機異常（虛構）");
        Assert.Contains("原因：行走馬達過載（虛構） → 減速機異常（虛構）", Inc.HistoryText);
        Assert.Contains("審核通過（第 1 版）", Inc.HistoryText);
    }

    [Fact]
    public async Task 放棄修改()
    {
        var id = await ReviewedRecord();
        Inc.ReviseCommand.Execute(null);
        Inc.EditCause = "改過（虛構）";

        Inc.CancelReviseCommand.Execute(null);

        Assert.Equal(IncidentStatus.Reviewed, OnDisk(id).Status);
        Assert.Empty(Inc.Queue);
        Assert.False(Inc.IsEditing);
    }

    [Fact]
    public async Task 停用需要理由_停用後查詢不列出_恢復後回到待審核清單()
    {
        var id = await ReviewedRecord();
        Assert.False(Inc.DisableCommand.CanExecute(null));

        Inc.DisableReason = "原因判斷錯誤（虛構）";
        Inc.DisableCommand.Execute(null);

        Assert.Empty(Inc.Results);
        Assert.Single(Inc.Disabled);
        Assert.Contains("停用理由：原因判斷錯誤（虛構）", Inc.Detail);
        Assert.True(Inc.RestoreCommand.CanExecute(null));

        Inc.RestoreCommand.Execute(null);

        Assert.Equal("恢復", Assert.Single(Inc.Queue).Kind);
        Assert.Empty(Inc.Results);
        Assert.Empty(Inc.Disabled);
        Assert.Equal(IncidentStatus.Draft, OnDisk(id).Status);
    }

    [Fact]
    public async Task 整理維修紀錄_沒有事件只提醒()
    {
        await OpenCase();

        Inc.NewMaintenanceCommand.Execute(null);
        FillRequired();

        Assert.Equal("維修紀錄", Assert.Single(Inc.Queue).Kind);
        Assert.Contains("維修紀錄沒有事件序號", Inc.CheckText);
        Assert.StartsWith("可以審核通過", Inc.CheckText);
    }

    [Fact]
    public async Task 另一台電腦先修改時不覆寫_重新載入後可以繼續()
    {
        var id = await ReviewedRecord();
        var store = new IncidentStore(Folder);
        var other = store.LoadAll().Records.Single();
        store.Save(IncidentWorkflow.Disable(other.Record, "另一台電腦停用", "丙", _now), other.Stamp);

        Inc.ReviseCommand.Execute(null);

        Assert.Contains("已被修改", Inc.Status);
        Assert.Equal(IncidentStatus.Disabled, OnDisk(id).Status);

        Inc.ReloadCommand.Execute(null);
        Assert.Single(Inc.Disabled);
        Assert.Empty(Inc.Results);
    }

    [Fact]
    public async Task 跳到事件_以穩定識別碼_其他案件的紀錄不能跳()
    {
        await ReviewedRecord();
        _vm.SelectedEvent = _vm.Events[0];

        Inc.SelectedRecordEvent = Inc.RecordEvents[0];
        Inc.GoToEventCommand.Execute(null);

        Assert.Contains("警報發生 SSCA01 E101", _vm.SelectedEvent!.Event.Message);

        // 另一個案件開啟時：紀錄仍可閱讀，但不能跳
        _vm.CloseCaseCommand.Execute(null);
        await OpenCase("另一個虛構案件");
        Inc.SelectedResult = Inc.Results[0];
        Inc.SelectedRecordEvent = Inc.RecordEvents[0];
        Inc.GoToEventCommand.Execute(null);
        Assert.Contains("不是目前開啟的案件", Inc.Status);
    }

    [Fact]
    public async Task 查詢只搜尋有效的版本()
    {
        await ReviewedRecord();
        Select("警報發生 SSCA01 E101");
        Inc.NewFromCaseCommand.Execute(null); // 另一筆草稿
        Inc.EditSymptom = "電梯停止（虛構）";
        Inc.SaveCommand.Execute(null);

        Inc.Query = "電梯";
        Inc.SearchCommand.Execute(null);
        Assert.Empty(Inc.Results);

        Inc.Query = "搬送機";
        Inc.SearchCommand.Execute(null);
        Assert.Single(Inc.Results);
    }

    [Fact]
    public async Task 待審核清單的等待時間()
    {
        await OpenCase();
        Inc.NewMaintenanceCommand.Execute(null);

        _now = _now.AddHours(26);
        Inc.ReloadCommand.Execute(null);

        Assert.Equal("1 天 2 小時", Inc.Queue[0].Waiting);
    }
}
