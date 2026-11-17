using LogTrace.Engine.Incidents;
using LogTrace.Tests.Support;

namespace LogTrace.Workspace.Tests;

// 異常紀錄面板的「總結」分頁（#168，ADR-0023 第 1 點）。測試資料：tests/fixtures/playback-prototype（虛構）
public sealed class PlantSummaryViewModelTests : IDisposable
{
    readonly TempFolder _tmp = new();
    readonly FakeUserInteraction _ui = new() { ConfirmResult = true };
    readonly MainViewModel _vm;
    readonly string _plants;
    DateTimeOffset _now = new(2030, 1, 11, 12, 0, 0, TimeSpan.FromHours(8));

    public PlantSummaryViewModelTests()
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

    string SummaryPath => Path.Combine(_plants, "範例廠.summary.json");

    async Task OpenCase()
    {
        var fixture = TempFolder.Fixture("playback-prototype");
        File.Copy(Path.Combine(fixture, "plant.json"), Path.Combine(_plants, "範例廠.json"));
        _vm.RefreshPlants();
        _vm.SelectedPlant = _vm.Plants.Single(p => p.Path is not null);
        _vm.NewCaseName = "總結";
        _vm.CreateCaseCommand.Execute(null);
        _vm.ImportSource = Path.Combine(fixture, "logs");
        await _vm.ImportCommand.ExecuteAsync(null);
    }

    string NewReviewed(string disposal)
    {
        _vm.SelectedEvent = _vm.Events.First(e => e.Event.Message.Contains("警報發生 SSCA01 E101"));
        Inc.NewFromCaseCommand.Execute(null);
        Inc.EditSymptom = "搬送機停止（虛構）";
        Inc.EditCause = "行走馬達過載（虛構）";
        Inc.EditDisposal = disposal;
        Inc.EditResult = "復歸後正常（虛構）";
        Inc.EditExecutor = "工程師";
        Inc.UserName = "乙";
        Inc.ApproveCommand.Execute(null);
        Inc.UserName = "甲";
        return Inc.SelectedId!;
    }

    [Fact]
    public async Task 沒有總結時提示_產生後成為草稿_列出排名與引用的紀錄()
    {
        await OpenCase();
        Assert.Contains("還沒有審核過的總結", Inc.SummaryStatus);
        var id1 = NewReviewed("檢查行走馬達（虛構）");
        var id2 = NewReviewed("檢查行走馬達（虛構）");

        Inc.GenerateSummaryCommand.Execute(null);

        Assert.True(File.Exists(SummaryPath));
        Assert.True(Inc.SummaryShowDraft);
        Assert.Contains("有一份草稿", Inc.SummaryStatus);
        Assert.Contains("涵蓋 2 筆有效紀錄", Inc.SummaryInfo);
        var e101 = Inc.SummaryRows.Single(r => r.Section == "警報代碼" && r.Key == "E101");
        Assert.Equal(2, e101.Count);
        Assert.Equal("檢查行走馬達（虛構）（2 筆）", e101.Disposals);
        Assert.Contains(Inc.SummaryRows, r => r.Section == "異常類型（推得）" && r.Key == PlantSummaries.KindAlarm);

        Inc.SelectedSummaryRow = e101;
        Assert.Equal(new[] { id1, id2 }.Order(), Inc.SummaryRecordIds.Order()); // 編號的亂數決定順序
        Inc.SelectedSummaryRecordId = id2;
        Assert.Equal(id2, Inc.SelectedId);
    }

    [Fact]
    public async Task 寫說明後審核通過_取代目前的總結_之後新增紀錄時提示筆數()
    {
        await OpenCase();
        NewReviewed("檢查行走馬達（虛構）");
        Inc.GenerateSummaryCommand.Execute(null);
        Inc.SummaryNote = "E101 多為行走馬達問題（虛構）";
        Inc.SaveSummaryNoteCommand.Execute(null);

        Inc.UserName = "乙";
        Inc.ApproveSummaryCommand.Execute(null);

        Assert.DoesNotContain("提醒", _ui.LastConfirmMessage);
        Assert.False(Inc.SummaryShowDraft);
        Assert.False(Inc.HasSummaryDraft);
        Assert.Equal("E101 多為行走馬達問題（虛構）", Inc.SummaryNote);
        Assert.Contains("審核：乙", Inc.SummaryInfo);
        Assert.Contains("之後新增或改版的已審核紀錄 0 筆", Inc.SummaryStatus);
        Assert.Contains("0 分鐘前", Inc.SummaryStatus);

        _now = _now.AddDays(3);
        Inc.UserName = "甲";
        NewReviewed("更換減速機（虛構）");
        Assert.Contains("3 天前", Inc.SummaryStatus);
        Assert.Contains("之後新增或改版的已審核紀錄 1 筆", Inc.SummaryStatus);
    }

    [Fact]
    public async Task 重新產生時舊的總結繼續有效_捨棄草稿後回到原本的總結()
    {
        await OpenCase();
        NewReviewed("檢查行走馬達（虛構）");
        Inc.GenerateSummaryCommand.Execute(null);
        Inc.SummaryNote = "第一版說明（虛構）";
        Inc.UserName = "乙";
        Inc.ApproveSummaryCommand.Execute(null);
        Inc.UserName = "甲";
        NewReviewed("更換減速機（虛構）");

        Inc.GenerateSummaryCommand.Execute(null);
        Assert.Equal("第一版說明（虛構）", Inc.SummaryNote); // 說明沿用
        Assert.Contains("涵蓋 2 筆", Inc.SummaryInfo);

        Inc.SummaryShowDraft = false;
        Assert.Contains("涵蓋 1 筆", Inc.SummaryInfo);
        Inc.SummaryShowDraft = true;
        Inc.DiscardSummaryDraftCommand.Execute(null);

        Assert.False(Inc.HasSummaryDraft);
        Assert.Contains("涵蓋 1 筆", Inc.SummaryInfo);
        Assert.Null(PlantSummaryStore.Load(SummaryPath).File!.Draft);
    }

    [Fact]
    public async Task 審核時提醒說明空白與同一人_取消時不通過()
    {
        await OpenCase();
        NewReviewed("檢查行走馬達（虛構）");
        Inc.GenerateSummaryCommand.Execute(null);

        _ui.ConfirmResult = false;
        Inc.ApproveSummaryCommand.Execute(null);

        Assert.Contains("說明空白", _ui.LastConfirmMessage);
        Assert.Contains("產生者與審核者同一人", _ui.LastConfirmMessage);
        Assert.Null(PlantSummaryStore.Load(SummaryPath).File!.Reviewed);
        Assert.True(Inc.HasSummaryDraft);
    }

    [Fact]
    public async Task 已審核的總結不能修改說明_別台電腦修改後不覆寫()
    {
        await OpenCase();
        NewReviewed("檢查行走馬達（虛構）");
        Inc.GenerateSummaryCommand.Execute(null);
        Inc.SummaryShowDraft = false;
        Assert.False(Inc.SaveSummaryNoteCommand.CanExecute(null));
        Assert.False(Inc.ApproveSummaryCommand.CanExecute(null));
        Inc.SummaryShowDraft = true;

        var (file, stamp) = PlantSummaryStore.Load(SummaryPath);
        PlantSummaryStore.Save(SummaryPath, PlantSummaries.SaveNote(file!, "別台電腦（虛構）"), stamp);
        Inc.SummaryNote = "這台（虛構）";
        Inc.SaveSummaryNoteCommand.Execute(null);

        Assert.Contains("已被別人修改", Inc.Status);
        Assert.Equal("別台電腦（虛構）", PlantSummaryStore.Load(SummaryPath).File!.Draft!.Note);
    }
}
