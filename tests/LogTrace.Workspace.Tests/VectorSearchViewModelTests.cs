using LogTrace.Engine.Incidents;
using LogTrace.Tests.Support;

namespace LogTrace.Workspace.Tests;

// 向量搜尋（#171，ADR-0022）：知識庫面板與異常紀錄面板的「意思相近（向量）」。
// 測試資料：tests/fixtures/playback-prototype 的 plant.json、plant.kb.csv 與 LOG（虛構）；embedding 用 FakeEmbedder，不連 Ollama。
public sealed class VectorSearchViewModelTests : IDisposable
{
    readonly TempFolder _tmp = new();
    readonly FakeUserInteraction _ui = new() { ConfirmResult = true };
    readonly MainViewModel _vm;
    readonly string _plants;
    readonly FakeEmbedder _embedder = new();

    public VectorSearchViewModelTests()
    {
        _vm = new MainViewModel(new SettingsStore(Path.Combine(_tmp.Path, "settings.json")), _ui, Path.Combine(_tmp.Path, "Cases"), _ => false);
        _plants = Directory.CreateDirectory(Path.Combine(_tmp.Path, "Plants")).FullName;
        _vm.Incidents.UserName = "甲";
    }

    public void Dispose()
    {
        _vm.Dispose();
        _tmp.Dispose();
    }

    Incidents.IncidentsViewModel Inc => _vm.Incidents;

    async Task OpenCase(bool withKnowledge = true)
    {
        var fixture = TempFolder.Fixture("playback-prototype");
        File.Copy(Path.Combine(fixture, "plant.json"), Path.Combine(_plants, "範例廠.json"));
        if (withKnowledge) File.Copy(Path.Combine(fixture, "plant.kb.csv"), Path.Combine(_plants, "範例廠.kb.csv"));
        _vm.RefreshPlants();
        _vm.SelectedPlant = _vm.Plants.Single(p => p.Path is not null);
        _vm.NewCaseName = "向量搜尋";
        _vm.CreateCaseCommand.Execute(null);
        _vm.ImportSource = Path.Combine(fixture, "logs");
        await _vm.ImportCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task 沒有設定embedding時不做向量搜尋()
    {
        await OpenCase();

        _vm.KnowledgeQuery = "光電";
        await _vm.SearchKnowledgeCommand.ExecuteAsync(null);

        Assert.Empty(_vm.KnowledgeSimilar);
        Assert.Equal("", _vm.KnowledgeSimilarStatus);
        Assert.False(File.Exists(Path.Combine(_plants, "範例廠.kb.vec")));
    }

    [Fact]
    public async Task 知識庫_關鍵字搜尋之外列出意思相近的前幾名_向量檔建在知識庫旁()
    {
        _vm.Embedder = _embedder;
        await OpenCase();

        _vm.KnowledgeQuery = "光電感測器被擋住";
        await _vm.SearchKnowledgeCommand.ExecuteAsync(null);

        Assert.Empty(_vm.KnowledgeResults); // 整句當關鍵字找不到
        Assert.Equal(3, _vm.KnowledgeSimilar.Count); // 知識庫只有 3 條，前 5 名即全部
        Assert.Equal("E301", _vm.KnowledgeSimilar[0].Code);
        Assert.Contains("意思相近（向量）前 3 筆", _vm.KnowledgeSimilarStatus);
        Assert.Contains("這次建立 3 筆向量", _vm.KnowledgeSimilarStatus);
        Assert.True(File.Exists(Path.Combine(_plants, "範例廠.kb.vec")));

        await _vm.SearchKnowledgeCommand.ExecuteAsync(null);
        Assert.DoesNotContain("這次建立", _vm.KnowledgeSimilarStatus);
    }

    [Fact]
    public async Task 知識庫_沒有關鍵字時不列出意思相近()
    {
        _vm.Embedder = _embedder;
        await OpenCase();

        _vm.KnowledgeQuery = "";
        await _vm.SearchKnowledgeCommand.ExecuteAsync(null);

        Assert.Empty(_vm.KnowledgeSimilar);
        Assert.Equal("", _vm.KnowledgeSimilarStatus);
    }

    [Fact]
    public async Task 知識庫_Ollama無法使用時顯示原因_關鍵字搜尋照常()
    {
        _embedder.Available = false;
        _vm.Embedder = _embedder;
        await OpenCase();

        _vm.KnowledgeQuery = "馬達";
        await _vm.SearchKnowledgeCommand.ExecuteAsync(null);

        Assert.Equal(2, _vm.KnowledgeResults.Count);
        Assert.Empty(_vm.KnowledgeSimilar);
        Assert.Contains("無法使用：連不到 Ollama", _vm.KnowledgeSimilarStatus);
        Assert.Contains("關鍵字搜尋照常可用", _vm.KnowledgeSimilarStatus);
    }

    [Fact]
    public async Task 知識庫_重新載入時清除意思相近()
    {
        _vm.Embedder = _embedder;
        await OpenCase();
        _vm.KnowledgeQuery = "光電";
        await _vm.SearchKnowledgeCommand.ExecuteAsync(null);
        Assert.NotEmpty(_vm.KnowledgeSimilar);

        _vm.ReloadKnowledgeCommand.Execute(null);

        Assert.Empty(_vm.KnowledgeSimilar);
        Assert.Equal("", _vm.KnowledgeSimilarStatus);
    }

    void NewRecord(string symptom, bool approve)
    {
        _vm.SelectedEvent = _vm.Events.First(e => e.Event.Message.Contains("警報發生 SSCA01 E101"));
        Inc.NewFromCaseCommand.Execute(null);
        Inc.EditSymptom = symptom;
        Inc.EditCause = "虛構原因";
        Inc.EditDisposal = "虛構處置";
        Inc.EditResult = "虛構結果";
        Inc.EditExecutor = "工程師";
        if (approve)
        {
            Inc.UserName = "乙";
            Inc.ApproveCommand.Execute(null);
            Inc.UserName = "甲";
        }
        else Inc.SaveCommand.Execute(null);
    }

    [Fact]
    public async Task 異常紀錄_依現象列出意思相近的有效紀錄_不列草稿()
    {
        _vm.Embedder = _embedder;
        await OpenCase();
        NewRecord("搬送機行走馬達過熱停止（虛構）", approve: true);
        NewRecord("條碼讀不到，貨物停在入庫口（虛構）", approve: true);
        NewRecord("條碼讀取器鏡頭髒污（虛構草稿）", approve: false);

        Inc.Query = "條碼掃不到";
        await Inc.SearchCommand.ExecuteAsync(null);

        Assert.Equal(2, Inc.SimilarResults.Count); // 草稿不列
        Assert.Equal("條碼讀不到，貨物停在入庫口（虛構）", Inc.SimilarResults[0].Symptom);
        Assert.Contains("過去相似的異常", Inc.SimilarStatus);
        Assert.True(File.Exists(Path.Combine(_plants, "範例廠.incidents", Incidents.IncidentsViewModel.VectorFileName)));
        Assert.Equal(2, new IncidentStore(Path.Combine(_plants, "範例廠.incidents")).LoadAll().Records.Count(r => r.Record.Status == IncidentStatus.Reviewed));

        Inc.SelectedSimilar = Inc.SimilarResults[0];
        Assert.Equal(Inc.SimilarResults[0].Id, Inc.SelectedId);
    }

    [Fact]
    public async Task 異常紀錄_無法使用時顯示原因()
    {
        _embedder.Available = false;
        _vm.Embedder = _embedder;
        await OpenCase();
        NewRecord("搬送機停止（虛構）", approve: true);

        Inc.Query = "搬送機";
        await Inc.SearchCommand.ExecuteAsync(null);

        Assert.Single(Inc.Results);
        Assert.Empty(Inc.SimilarResults);
        Assert.Contains("無法使用", Inc.SimilarStatus);
    }

    [Fact]
    public async Task 異常紀錄_勾選顯示已過時時意思相近也包含已過時_切換時清除()
    {
        _vm.Embedder = _embedder;
        await OpenCase();
        NewRecord("條碼讀不到，貨物停在入庫口（虛構）", approve: true);
        Inc.ObsoleteReason = "讀取器已更換（虛構）";
        Inc.MarkObsoleteCommand.Execute(null);
        NewRecord("搬送機行走馬達過熱停止（虛構）", approve: true);

        Inc.Query = "條碼掃不到";
        await Inc.SearchCommand.ExecuteAsync(null);
        Assert.Equal(["搬送機行走馬達過熱停止（虛構）"], Inc.SimilarResults.Select(r => r.Symptom)); // 預設不含已過時

        Inc.ShowObsolete = true;
        Assert.Empty(Inc.SimilarResults);
        Assert.Equal("", Inc.SimilarStatus);

        await Inc.SearchCommand.ExecuteAsync(null);
        Assert.Equal(2, Inc.SimilarResults.Count);
        Assert.Equal("條碼讀不到，貨物停在入庫口（虛構）", Inc.SimilarResults[0].Symptom);
        Assert.Equal("已過時", Inc.SimilarResults[0].Status);
    }
}
