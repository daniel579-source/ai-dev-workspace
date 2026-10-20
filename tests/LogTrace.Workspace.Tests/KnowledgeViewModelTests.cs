using LogTrace.Tests.Support;

namespace LogTrace.Workspace.Tests;

// 知識庫第一版（#80，ADR-0013 第 2 點）。測試資料：tests/fixtures/playback-prototype 的 plant.json 與 plant.kb.csv（虛構）
public sealed class KnowledgeViewModelTests : IDisposable
{
    readonly TempFolder _tmp = new();
    readonly MainViewModel _vm;
    readonly string _plants;

    public KnowledgeViewModelTests()
    {
        _vm = new MainViewModel(new SettingsStore(Path.Combine(_tmp.Path, "settings.json")), new FakeUserInteraction(), Path.Combine(_tmp.Path, "Cases"), _ => false);
        _plants = Directory.CreateDirectory(Path.Combine(_tmp.Path, "Plants")).FullName;
    }

    public void Dispose()
    {
        _vm.Dispose();
        _tmp.Dispose();
    }

    string KbPath => Path.Combine(_plants, "範例廠.kb.csv");

    async Task OpenCase(bool withKnowledge = true, bool builtIn = false)
    {
        var fixture = TempFolder.Fixture("playback-prototype");
        if (!builtIn) File.Copy(Path.Combine(fixture, "plant.json"), Path.Combine(_plants, "範例廠.json")); // 有設定檔時選內建預設會先詢問
        if (withKnowledge) File.Copy(Path.Combine(fixture, "plant.kb.csv"), KbPath);
        _vm.RefreshPlants();
        _vm.SelectedPlant = builtIn ? _vm.Plants.Single(p => p.Path is null) : _vm.Plants.Single(p => p.Path is not null);
        _vm.NewCaseName = "知識庫";
        _vm.CreateCaseCommand.Execute(null);
        _vm.ImportSource = Path.Combine(fixture, "logs");
        await _vm.ImportCommand.ExecuteAsync(null);
    }

    void Select(string text) => _vm.SelectedEvent = _vm.Events.First(e => e.Event.Message.Contains(text));

    [Fact]
    public async Task 開啟案件時讀取廠別的知識庫_面板列出全部()
    {
        await OpenCase();

        Assert.Equal(["E101", "E101", "E301"], _vm.KnowledgeResults.Select(e => e.Code));
        Assert.Contains("3 筆", _vm.KnowledgeStatus);
        Assert.Contains("範例廠.kb.csv", _vm.KnowledgeStatus);
        Assert.Empty(_vm.KnowledgeIssues);
    }

    [Fact]
    public async Task 詳細資訊顯示警報代碼的說明處置與出處_同一代碼多列都顯示()
    {
        await OpenCase();

        Select("警報發生 SSCA01 E101");

        var detail = _vm.SelectedDetail;
        Assert.Contains("知識庫（警報代碼 E101）：", detail);
        Assert.Contains("說明：搬送機行走馬達過載（虛構）", detail);
        Assert.Contains("處置：停止搬送機後檢查行走馬達與減速機", detail);
        Assert.Contains("出處：虛構維修手冊 第 3 章", detail);
        Assert.Contains("說明：站點輸送馬達過載（虛構；站點發生時）", detail);
        Assert.EndsWith("警報發生 SSCA01 E101", detail.TrimEnd()); // 訊息仍在最後（醒目標示的位置依此計算）
    }

    [Fact]
    public async Task 代碼不在知識庫中_明確說明_不推測()
    {
        await OpenCase();

        Select("警報發生 A-02 E205");

        Assert.Contains("知識庫（警報代碼 E205）：知識庫中沒有這個代碼", _vm.SelectedDetail);
        Assert.DoesNotContain("說明：", _vm.SelectedDetail);
    }

    [Fact]
    public async Task 沒有警報代碼的事件_不顯示知識庫()
    {
        await OpenCase();

        Select("讀到棧板條碼 PLT-EX-0001");

        Assert.DoesNotContain("知識庫", _vm.SelectedDetail);
    }

    [Fact]
    public async Task 廠別沒有知識庫檔時_面板與詳細資訊都說明_其他功能照常()
    {
        await OpenCase(withKnowledge: false);

        Assert.Contains("這個廠別沒有知識庫", _vm.KnowledgeStatus);
        Assert.Contains("範例廠.kb.csv", _vm.KnowledgeStatus); // 告訴使用者可以放在哪裡
        Assert.Empty(_vm.KnowledgeResults);
        Select("警報發生 SSCA01 E101");
        Assert.Contains("知識庫：這個廠別沒有知識庫", _vm.SelectedDetail);
    }

    [Fact]
    public async Task 內建預設的案件沒有知識庫()
    {
        await OpenCase(builtIn: true);

        Assert.Contains("這個廠別沒有知識庫", _vm.KnowledgeStatus);
    }

    [Fact]
    public async Task 關鍵字搜尋_不分大小寫與全半形()
    {
        await OpenCase();

        _vm.KnowledgeQuery = "馬達 ｅ101";
        _vm.SearchKnowledgeCommand.Execute(null);

        Assert.Equal(2, _vm.KnowledgeResults.Count);
        Assert.Contains("符合 2 筆", _vm.KnowledgeStatus);

        _vm.KnowledgeQuery = "";
        _vm.SearchKnowledgeCommand.Execute(null);
        Assert.Equal(3, _vm.KnowledgeResults.Count);
    }

    [Fact]
    public async Task 重新載入_讀到修改後的內容_格式有誤的列列出行號與原因()
    {
        await OpenCase();

        File.WriteAllText(KbPath, "代碼,說明,處置,出處\nE205,站點輸送逾時（虛構）,,\nE999,,,\n");
        _vm.ReloadKnowledgeCommand.Execute(null);

        Assert.Equal(["E205"], _vm.KnowledgeResults.Select(e => e.Code));
        Assert.Equal(["第 3 行：沒有填說明"], _vm.KnowledgeIssues);
        Assert.Contains("1 列格式有誤", _vm.KnowledgeStatus);
        Select("警報發生 A-02 E205");
        Assert.Contains("說明：站點輸送逾時（虛構）", _vm.SelectedDetail);
    }

    [Fact]
    public async Task 關閉案件後清空()
    {
        await OpenCase();

        _vm.CloseCaseCommand.Execute(null);

        Assert.Empty(_vm.KnowledgeResults);
        Assert.Equal("請開啟案件。", _vm.KnowledgeStatus);
        Assert.False(_vm.ReloadKnowledgeCommand.CanExecute(null));
    }
}
