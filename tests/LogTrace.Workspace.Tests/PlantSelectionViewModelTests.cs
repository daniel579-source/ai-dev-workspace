using LogTrace.Engine.Plants;
using LogTrace.Tests.Support;
using LogTrace.Workspace.Layout;

namespace LogTrace.Workspace.Tests;

// 建立案件時選擇廠別、編輯器的 LOG 設定（#34）。內容全部是虛構的。
public sealed class PlantSelectionViewModelTests : IDisposable
{
    readonly TempFolder _tmp = new();
    readonly FakeUserInteraction _ui = new();
    readonly string _plantsRoot;
    readonly MainViewModel _vm;

    public PlantSelectionViewModelTests()
    {
        _plantsRoot = Path.Combine(_tmp.Path, "Plants");
        _vm = new MainViewModel(new SettingsStore(Path.Combine(_tmp.Path, "settings.json")), _ui, Path.Combine(_tmp.Path, "Cases"), _ => false);
    }

    public void Dispose()
    {
        _vm.Dispose();
        _tmp.Dispose();
    }

    string SavePlant(string name, Action<PlantConfigDocument>? edit = null)
    {
        var doc = new PlantConfigDocument { Plant = name };
        edit?.Invoke(doc);
        var path = Path.Combine(_plantsRoot, name + ".json");
        PlantConfigFile.Save(doc, path);
        return path;
    }

    [Fact]
    public void 沒有廠別設定時_可以用內建預設值建立案件()
    {
        Assert.Equal([PlantChoiceViewModel.BuiltIn], _vm.Plants);
        Assert.Same(PlantChoiceViewModel.BuiltIn, _vm.SelectedPlant);

        _vm.NewCaseName = "甲";
        _vm.CreateCaseCommand.Execute(null);

        Assert.Equal("內建預設", _vm.CurrentCasePlant);
        Assert.Contains("廠別：內建預設", _vm.Status);
    }

    [Fact]
    public async Task 選擇廠別設定建立案件_匯入使用該廠別的模組順序()
    {
        SavePlant("範例廠", d => d.ModuleOrder = ["PLC", "EXE", "Server"]);
        _vm.RefreshPlants();
        _vm.SelectedPlant = _vm.Plants.Single(p => p.Name == "範例廠");

        _vm.NewCaseName = "甲";
        _vm.CreateCaseCommand.Execute(null);
        _vm.ImportSource = TempFolder.Fixture("two-modules");
        await _vm.ImportCommand.ExecuteAsync(null);

        Assert.Equal("範例廠（修訂 1）", _vm.CurrentCasePlant);
        Assert.Equal(["PLC", "EXE"], _vm.Events.Take(2).Select(e => e.Module));
        Assert.Equal(["PLC", "EXE"], _vm.Modules); // 分軌檢視的順序也依廠別設定
        Assert.Equal("範例廠", _vm.Cases.Single().PlantName);
    }

    [Fact]
    public void 重新列出廠別設定時_保留原本的選擇()
    {
        var path = SavePlant("乙廠");
        _vm.RefreshPlants();
        _vm.SelectedPlant = _vm.Plants.Single(p => p.Path == path);

        SavePlant("甲廠");
        _vm.RefreshPlants();

        Assert.Equal(3, _vm.Plants.Count);
        Assert.Equal(path, _vm.SelectedPlant!.Path);
    }

    [Fact]
    public void 廠別設定檔讀不了時_建立案件失敗並說明原因()
    {
        var path = Path.Combine(_plantsRoot, "新版.json");
        Directory.CreateDirectory(_plantsRoot);
        File.WriteAllText(path, """{ "formatVersion": 9, "plant": "虛構" }""");
        _vm.RefreshPlants();
        _vm.SelectedPlant = _vm.Plants.Single(p => p.Path == path);

        _vm.NewCaseName = "甲";
        _vm.CreateCaseCommand.Execute(null);

        Assert.Null(_vm.CurrentCaseFolder);
        Assert.Contains("格式版本是 9", _vm.Status);
    }

    [Fact]
    public void 編輯器的LOG設定_模組順序_已知LOG種類_編碼_存檔後讀回()
    {
        var editor = new LayoutEditorViewModel(new PlantConfigLibrary(_plantsRoot), _ui) { PlantName = "範例廠" };
        Assert.Equal("EXE\r\nPLC\r\nServer".ReplaceLineEndings(), editor.ModuleOrderText.ReplaceLineEndings()); // 新設定從內建預設值開始

        editor.ModuleOrderText = "PLC\nEXE\n\n Server ";
        editor.KnownLoggersText = "PLCLOG\r\nWCSCMD";
        editor.AddEncodingRuleCommand.Execute(null);
        editor.EncodingRules[0].Module = "PLC";
        editor.EncodingRules[0].Logger = "PLCLOG";
        editor.AddEncodingRuleCommand.Execute(null);
        editor.EncodingRules[1].Logger = "WCSCMD";
        editor.EncodingRules[1].Encoding = "UTF-8";
        editor.AddEncodingRuleCommand.Execute(null);
        editor.RemoveEncodingRuleCommand.Execute(editor.EncodingRules[2]);
        Assert.True(editor.IsDirty);
        editor.SaveCommand.Execute(null);

        var doc = PlantConfigFile.Load(editor.FilePath!);
        Assert.Equal(["PLC", "EXE", "Server"], doc.ModuleOrder);
        Assert.Equal(["PLCLOG", "WCSCMD"], doc.KnownLoggers);
        Assert.Equal([new EncodingRule("PLC", "PLCLOG", "Big5"), new EncodingRule(null, "WCSCMD", "UTF-8")], doc.Encodings);

        var reopened = new LayoutEditorViewModel(new PlantConfigLibrary(_plantsRoot), _ui);
        reopened.OpenConfigCommand.Execute(editor.FilePath);
        Assert.Equal(2, reopened.EncodingRules.Count);
        Assert.Equal("PLC", reopened.EncodingRules[0].Module);
        Assert.False(reopened.IsDirty);
    }

    [Fact]
    public void 編碼設定沒有填Logger時_無法存檔()
    {
        var editor = new LayoutEditorViewModel(new PlantConfigLibrary(_plantsRoot), _ui) { PlantName = "範例廠" };
        editor.AddEncodingRuleCommand.Execute(null);

        editor.SaveCommand.Execute(null);

        Assert.Null(editor.FilePath);
        Assert.Contains("沒有填 Logger", editor.Status);
    }
}
