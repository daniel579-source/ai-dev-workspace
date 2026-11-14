using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using LogTrace.Tests.Support;

namespace LogTrace.App.SmokeTests;

// 解析欄位的篩選、把未對應的編號拖到元素上（#23）。測試資料：tests/fixtures/fields-cases（虛構）
[Trait("Category", "UI")]
public sealed class ParsedFieldsSmokeTests : IDisposable
{
    readonly TempFolder _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void 依Layout元素篩選事件_把未對應的編號拖到站點上成為別名()
    {
        var dataRoot = Path.Combine(_tmp.Path, "AppData");
        Directory.CreateDirectory(Path.Combine(dataRoot, "Plants"));
        File.WriteAllText(Path.Combine(dataRoot, "Plants", "範例廠.json"), """
            { "formatVersion": 1, "plant": "範例廠", "revision": 1,
              "layout": { "elements": [
                { "id": "st-1", "type": "station", "name": "入庫口1", "aliases": ["A-01"], "x": 2, "y": 2, "w": 3, "h": 3, "rotation": 0 },
                { "id": "vh-1", "type": "vehicle", "name": "搬送機1", "aliases": ["SRCA01"], "x": 8, "y": 2, "w": 3, "h": 2, "rotation": 0 } ] } }
            """);

        using var app = FlaUI.Core.Application.Launch(new ProcessStartInfo(ImportSmokeTests.AppExe(), $"--data-root \"{dataRoot}\""));
        using var automation = new UIA3Automation();
        try
        {
            var main = app.GetMainWindow(automation, TimeSpan.FromSeconds(30))!;
            var cf = main.ConditionFactory;
            AutomationElement In(AutomationElement root, string id) =>
                Retry.WhileNull(() => root.FindFirstDescendant(cf.ByAutomationId(id)), TimeSpan.FromSeconds(10)).Result!;

            var plants = In(main, "PlantChoice").AsComboBox();
            plants.Select("範例廠");
            In(main, "NewCaseName").AsTextBox().Text = "欄位案件";
            In(main, "CreateCase").AsButton().Invoke();
            In(main, "ImportSource").AsTextBox().Text = TempFolder.Fixture("fields-cases");
            In(main, "Import").AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => In(main, "Status").Name.Contains("案件共 11 筆事件"), TimeSpan.FromSeconds(30)).Result, In(main, "Status").Name);
            // 「搜尋」面板（與「案件」並列的分頁）→ 篩選條件 → Layout 元素
            ImportSmokeTests.OpenTab(main, "搜尋");
            In(main, "SearchFilters").Patterns.ExpandCollapse.Pattern.Expand();
            var kind = In(main, "FilterKind").AsComboBox();
            kind.Select("Layout 元素");
            In(main, "FilterElement").AsComboBox().Select(0);
            In(main, "Search").AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => In(main, "SearchStatus").Name.Contains("符合 2 筆"), TimeSpan.FromSeconds(10)).Result, In(main, "SearchStatus").Name);

            In(main, "OpenLayoutEditor").AsButton().Invoke();
            var editor = Retry.WhileNull(() => main.FindFirstChild(cf.ByAutomationId("LayoutEditor")) ?? automation.GetDesktop().FindFirstChild(cf.ByAutomationId("LayoutEditor")), TimeSpan.FromSeconds(10)).Result!;
            var cfg = Retry.WhileNull(() => In(editor, "ConfigList").FindFirstChild(cf.ByControlType(ControlType.ListItem)), TimeSpan.FromSeconds(10)).Result!;
            cfg.AsListBoxItem().Select();
            In(editor, "OpenSelectedConfig").AsButton().Invoke();
            In(editor, "CompareCase").AsComboBox().Select(0);
            In(editor, "LoadCaseCompare").AsButton().Invoke();
            var a09 = Retry.WhileNull(() => In(editor, "UnmappedList").FindAllDescendants().FirstOrDefault(e => e.Name.Contains("A-09")), TimeSpan.FromSeconds(5)).Result!;
            // 在 3D 畫布上選取元素，再雙擊未對應的編號（#117：WebView2 收不到 WPF 的拖放）
            var target = Retry.WhileNull(() => In(editor, "Editor3D").FindFirstDescendant(cf.ByName("入庫口1")), TimeSpan.FromSeconds(30)).Result
                ?? throw new InvalidOperationException("3D 畫布中找不到「入庫口1」");
            target.Click();
            Assert.True(Retry.WhileFalse(() => In(editor, "ElementName").AsTextBox().Text == "入庫口1", TimeSpan.FromSeconds(10)).Result);
            a09.DoubleClick();
            Assert.True(Retry.WhileFalse(() => In(editor, "EditorStatus").Name.Contains("已把「A-09」加為「入庫口1」的別名"), TimeSpan.FromSeconds(10)).Result,
                In(editor, "EditorStatus").Name);
            Assert.DoesNotContain(In(editor, "UnmappedList").FindAllDescendants(), e => e.Name.Contains("A-09"));
        }
        finally
        {
            app.Kill();
        }
    }
}
