using LogTrace.Engine.Cases;
using LogTrace.Engine.Parsing;
using LogTrace.Engine.Plants;
using LogTrace.Tests.Support;
using LogTrace.Workspace.Layout;

namespace LogTrace.Workspace.Tests;

// 解析欄位的篩選、未對應的編號（#23）。測試資料：tests/fixtures/fields-cases（虛構）
public sealed class ParsedFieldsViewModelTests : IDisposable
{
    readonly TempFolder _tmp = new();
    readonly FakeUserInteraction _ui = new();
    readonly string _plantsRoot;
    readonly string _casesRoot;
    readonly MainViewModel _vm;

    public ParsedFieldsViewModelTests()
    {
        _plantsRoot = Path.Combine(_tmp.Path, "Plants");
        _casesRoot = Path.Combine(_tmp.Path, "Cases");
        _vm = new MainViewModel(new SettingsStore(Path.Combine(_tmp.Path, "settings.json")), _ui, _casesRoot, _ => false);
    }

    public void Dispose()
    {
        _vm.Dispose();
        _tmp.Dispose();
    }

    /// <summary>虛構的廠別：入庫口1 有 PLC 與 WCS 兩個別名；另外加一條數字型站點的自訂規則。</summary>
    string SavePlant()
    {
        var doc = new PlantConfigDocument
        {
            Plant = "範例廠",
            Layout = new LayoutSection
            {
                Elements =
                [
                    new() { Id = "st-1", Type = LayoutElementType.Station, Name = "入庫口1", Aliases = ["A-01", "1001"], X = 1, Y = 1, W = 2, H = 2, Rotation = 0 },
                    new() { Id = "vh-1", Type = LayoutElementType.Vehicle, Name = "搬送機1", Aliases = ["SRCA01"], X = 5, Y = 1, W = 2, H = 1, Rotation = 0 },
                ],
            },
        };
        doc.ParseRules.Add(new ParseRule("站點模式", null, null, @"站點 (?<station>[0-9]{4}) 模式=(?<stationMode>[0-9])"));
        var path = Path.Combine(_plantsRoot, "範例廠.json");
        PlantConfigFile.Save(doc, path);
        return path;
    }

    async Task ImportWithPlant()
    {
        var path = SavePlant();
        _vm.RefreshPlants();
        _vm.SelectedPlant = _vm.Plants.Single(p => p.Path == path);
        _vm.NewCaseName = "欄位";
        _vm.CreateCaseCommand.Execute(null);
        _vm.ImportSource = TempFolder.Fixture("fields-cases");
        await _vm.ImportCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task 詳細資訊顯示解析欄位()
    {
        await ImportWithPlant();

        _vm.SelectedEvent = _vm.Events.Single(e => e.Event.Message.Contains("卸貨完成"));

        Assert.Contains("解析欄位：命令 00030001、站點 A-02、棧板 123456789", _vm.SelectedDetail);
    }

    [Fact]
    public async Task 依Layout元素篩選_比對元素的所有別名()
    {
        await ImportWithPlant();
        Assert.Equal(["入庫口1", "搬送機1"], _vm.ElementChoices.Select(e => e.Name));

        _vm.SelectedFilter = _vm.FilterOptions.Single(f => f.Kind == FieldFilterKind.Element);
        _vm.SelectedFilterElement = _vm.ElementChoices[0];
        await _vm.SearchCommand.ExecuteAsync(null);

        // A-01 兩筆（PLC）＋ 1001 一筆（站點模式；路徑規劃中的「來源=1001」是來源欄位，不算站點）
        Assert.Equal(3, _vm.Events.Count);
        Assert.Contains(_vm.Events, e => e.Event.Message.Contains("模式=2"));
        Assert.Contains("符合 3 筆", _vm.SearchStatus);

        _vm.ClearSearchCommand.Execute(null);
        Assert.Equal(11, _vm.Events.Count);
        Assert.Null(_vm.SearchStatus);
    }

    [Fact]
    public async Task 依命令編號篩選_選取的事件在結果中時保持選取()
    {
        await ImportWithPlant();
        var selected = _vm.Events.Single(e => e.Event.Message.Contains("卸貨完成"));
        _vm.SelectedEvent = selected;

        _vm.SelectedFilter = _vm.FilterOptions.Single(f => f.Kind == FieldFilterKind.Command);
        _vm.FilterValue = "00030001";
        await _vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal(2, _vm.Events.Count);
        Assert.Same(selected, _vm.SelectedEvent);
    }

    [Fact]
    public async Task 內建預設的案件沒有Layout元素可以篩選_仍可依命令篩選()
    {
        _vm.NewCaseName = "預設";
        _vm.CreateCaseCommand.Execute(null);
        _vm.ImportSource = TempFolder.Fixture("fields-cases");
        await _vm.ImportCommand.ExecuteAsync(null);

        Assert.Empty(_vm.ElementChoices);
        _vm.SelectedFilter = _vm.FilterOptions.Single(f => f.Kind == FieldFilterKind.Element);
        await _vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal("請選擇 Layout 元素。", _vm.SearchStatus);
        _vm.SelectedFilter = _vm.FilterOptions.Single(f => f.Kind == FieldFilterKind.Pallet);
        _vm.FilterValue = "123456789";
        await _vm.SearchCommand.ExecuteAsync(null);
        Assert.Single(_vm.Events);
    }

    [Fact]
    public async Task 編輯器載入案件比對_未對應的編號_拖放成別名並存檔後從清單消失()
    {
        await ImportWithPlant();
        var caseFolder = _vm.CurrentCaseFolder!;
        var editor = new LayoutEditorViewModel(new PlantConfigLibrary(_plantsRoot), _ui, new CaseLibrary(_casesRoot));
        editor.OpenConfigCommand.Execute(Path.Combine(_plantsRoot, "範例廠.json"));
        editor.SelectedCompareCase = editor.CaseChoices.Single(c => c.Folder == caseFolder);

        editor.LoadCaseCompareCommand.Execute(null);

        // A-01、1001、SRCA01 已經是別名；其餘依出現次數排序
        Assert.Equal(["SRCA02", "A-02", "A-09"], editor.Unmapped.Select(u => u.Value));
        Assert.Equal(("搬送機", 4L), (editor.Unmapped[0].KindLabel, editor.Unmapped[0].Count));

        var station = editor.Elements.Single(e => e.Name == "入庫口1");
        Assert.True(editor.AssignUnmapped(editor.Unmapped.Single(u => u.Value == "A-09"), station));
        Assert.DoesNotContain(editor.Unmapped, u => u.Value == "A-09");
        editor.SaveCommand.Execute(null);

        var reopened = new LayoutEditorViewModel(new PlantConfigLibrary(_plantsRoot), _ui, new CaseLibrary(_casesRoot));
        reopened.OpenConfigCommand.Execute(editor.FilePath);
        reopened.SelectedCompareCase = reopened.CaseChoices.Single();
        reopened.LoadCaseCompareCommand.Execute(null);
        Assert.Equal(["SRCA02", "A-02"], reopened.Unmapped.Select(u => u.Value));
        Assert.Contains("A-09", reopened.Elements.Single(e => e.Name == "入庫口1").Aliases);
    }

    [Fact]
    public async Task 拖放到已有該別名的元素以外_別名已屬於其他元素時擋下()
    {
        await ImportWithPlant();
        var editor = new LayoutEditorViewModel(new PlantConfigLibrary(_plantsRoot), _ui, new CaseLibrary(_casesRoot));
        editor.OpenConfigCommand.Execute(Path.Combine(_plantsRoot, "範例廠.json"));
        var vehicle = editor.Elements.Single(e => e.Name == "搬送機1");

        Assert.False(editor.AssignUnmapped(new UnmappedIdViewModel(FieldKinds.Station, "a-01", 1), vehicle));
        Assert.Contains("已經屬於「入庫口1」", editor.Status);
    }

    [Fact]
    public void 編輯器的解析規則_新增_還原初始規則_有誤時無法存檔()
    {
        var editor = new LayoutEditorViewModel(new PlantConfigLibrary(_plantsRoot), _ui) { PlantName = "範例廠" };
        Assert.Equal(PlantConfig.DefaultParseRules.Count, editor.ParseRules.Count);

        editor.NewParseRuleCommand.Execute(null);
        editor.ParseRules[^1].Pattern = "(?<dock>EQ[0-9]{3})";
        editor.ParseRules[^1].Logger = "EXEDebug";
        Assert.Equal("signal", editor.ParseRules[2].Effect); // 初始規則的效果也要保留
        editor.SaveCommand.Execute(null);
        var saved = PlantConfigFile.Load(editor.FilePath!).ParseRules[^1];
        Assert.Equal(new ParseRule($"規則 {PlantConfig.DefaultParseRules.Count + 1}", null, "EXEDebug", "(?<dock>EQ[0-9]{3})"), saved);

        editor.ParseRules[^1].Pattern = "(?<dock>EQ[";
        editor.SaveCommand.Execute(null);
        Assert.Contains("正規表示式有誤", editor.Status);

        editor.ResetParseRulesCommand.Execute(null);
        Assert.Equal(PlantConfig.DefaultParseRules, editor.ParseRules.Select(r => r.ToModel()));
    }
}
