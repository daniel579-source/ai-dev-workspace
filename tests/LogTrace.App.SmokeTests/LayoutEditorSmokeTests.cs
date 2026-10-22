using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using LogTrace.Tests.Support;

namespace LogTrace.App.SmokeTests;

[Trait("Category", "UI")]
public sealed class LayoutEditorSmokeTests : IDisposable
{
    readonly TempFolder _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void 開啟編輯器_新增元素_存檔_重新開啟仍在()
    {
        var dataRoot = Path.Combine(_tmp.Path, "AppData");
        using var app = FlaUI.Core.Application.Launch(new ProcessStartInfo(ImportSmokeTests.AppExe(), $"--data-root \"{dataRoot}\""));
        using var automation = new UIA3Automation();
        try
        {
            var main = app.GetMainWindow(automation, TimeSpan.FromSeconds(30));
            Assert.NotNull(main);
            var cf = main.ConditionFactory;
            var desktop = automation.GetDesktop();
            AutomationElement In(AutomationElement root, string id) =>
                Retry.WhileNull(() => root.FindFirstDescendant(cf.ByAutomationId(id)), TimeSpan.FromSeconds(10)).Result
                ?? throw new InvalidOperationException($"找不到 {id}");
            // 編輯器是主視窗擁有的視窗，在 UI Automation 中可能是主視窗的子項，也可能是桌面的子項
            AutomationElement? FindEditor() =>
                main.FindFirstChild(cf.ByAutomationId("LayoutEditor")) ?? desktop.FindFirstChild(cf.ByAutomationId("LayoutEditor"));
            AutomationElement OpenEditor()
            {
                In(main, "OpenLayoutEditor").AsButton().Invoke();
                return Retry.WhileNull(() => FindEditor(), TimeSpan.FromSeconds(10)).Result
                    ?? throw new InvalidOperationException("找不到 Layout 編輯器視窗");
            }

            var editor = OpenEditor();
            In(editor, "PlantName").AsTextBox().Text = "冒煙測試廠";
            In(editor, "Add_Station").AsButton().Invoke();
            In(editor, "NewAlias").AsTextBox().Text = "A-01";
            In(editor, "AddAlias").AsButton().Invoke();
            In(editor, "SaveConfig").AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => In(editor, "EditorStatus").Name.Contains("修訂 1"), TimeSpan.FromSeconds(10)).Result,
                In(editor, "EditorStatus").Name);
            Assert.True(File.Exists(Path.Combine(dataRoot, "Plants", "冒煙測試廠.json")));
            editor.AsWindow().Close();
            Assert.True(Retry.WhileFalse(() => FindEditor() is null, TimeSpan.FromSeconds(10)).Result);

            // 回到主畫面後，建立案件的「廠別」清單出現剛存檔的廠別設定
            var plants = In(main, "PlantChoice").AsComboBox();
            Assert.True(Retry.WhileFalse(() => plants.Items.Any(i => i.Text == "冒煙測試廠"), TimeSpan.FromSeconds(10)).Result);
            plants.Collapse();

            editor = OpenEditor();
            var config = Retry.WhileNull(() => In(editor, "ConfigList").FindFirstChild(cf.ByControlType(ControlType.ListItem)), TimeSpan.FromSeconds(10)).Result;
            Assert.NotNull(config);
            config.AsListBoxItem().Select();
            In(editor, "OpenSelectedConfig").AsButton().Invoke();

            var item = Retry.WhileNull(() => In(editor, "ElementList").FindFirstChild(cf.ByControlType(ControlType.ListItem)), TimeSpan.FromSeconds(10)).Result;
            Assert.NotNull(item);
            var texts = string.Concat(item.FindAllDescendants().Select(e => e.Name));
            Assert.Contains("新站點 1", texts);
            Assert.Contains("A-01", texts);
            Assert.Contains("冒煙測試廠", In(editor, "PlantName").AsTextBox().Text);
            Assert.Contains("修訂 1", In(editor, "RevisionInfo").Name);
            // 畫布上也有（元素清單與畫布各一個名稱）
            Assert.True(editor.FindAllDescendants(cf.ByName("新站點 1")).Length >= 2);

            // 「符合視窗」：整張畫布（最小 60 × 40 公尺）放進可視範圍，縮放小於 100%（ADR-0006 第 11 點）
            Assert.Equal("100%", In(editor, "EditorZoomText").Name);
            In(editor, "EditorZoomFit").AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => In(editor, "EditorZoomText").Name != "100%", TimeSpan.FromSeconds(5)).Result);
            Assert.True(int.Parse(In(editor, "EditorZoomText").Name.TrimEnd('%')) < 100);

            // 點畫布上的元素後 Ctrl+C／Ctrl+V／Del／Ctrl+Z（ADR-0006 第 10 點）
            int Count() => In(editor, "ElementList").FindAllChildren(cf.ByControlType(ControlType.ListItem)).Length;
            bool Becomes(int n) => Retry.WhileFalse(() => Count() == n, TimeSpan.FromSeconds(5)).Result;
            // 畫布（Canvas）不在 UI Automation 樹中：取元素清單以外、名稱相同的那一個
            var listRect = In(editor, "ElementList").BoundingRectangle;
            var shape = editor.FindAllDescendants(cf.ByName("新站點 1")).First(x => !listRect.Contains(x.BoundingRectangle.Center()));
            Mouse.Click(shape.BoundingRectangle.Center());
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_C);
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_V);
            Assert.True(Becomes(2), In(editor, "EditorStatus").Name);
            Keyboard.Type(VirtualKeyShort.DELETE); // 刪除貼上後選取的新元素
            Assert.True(Becomes(1), In(editor, "EditorStatus").Name);
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_Z);
            Assert.True(Becomes(2), In(editor, "EditorStatus").Name);
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_Z); // 復原貼上
            Assert.True(Becomes(1), In(editor, "EditorStatus").Name);
            In(editor, "SaveConfig").AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => In(editor, "EditorStatus").Name.Contains("修訂 2"), TimeSpan.FromSeconds(10)).Result,
                In(editor, "EditorStatus").Name);
            editor.AsWindow().Close();
        }
        finally
        {
            app.Close();
            if (!app.HasExited) app.Kill();
        }
    }

    [Fact]
    public void 沒有LOG的門檻_從下拉選單選擇模組_存檔後重新開啟仍在()
    {
        var dataRoot = Path.Combine(_tmp.Path, "AppData");
        using var app = FlaUI.Core.Application.Launch(new ProcessStartInfo(ImportSmokeTests.AppExe(), $"--data-root \"{dataRoot}\""));
        using var automation = new UIA3Automation();
        try
        {
            var main = app.GetMainWindow(automation, TimeSpan.FromSeconds(30));
            Assert.NotNull(main);
            var cf = main.ConditionFactory;
            var desktop = automation.GetDesktop();
            AutomationElement In(AutomationElement root, string id) =>
                Retry.WhileNull(() => root.FindFirstDescendant(cf.ByAutomationId(id)), TimeSpan.FromSeconds(10)).Result
                ?? throw new InvalidOperationException($"找不到 {id}");
            AutomationElement? FindEditor() =>
                main.FindFirstChild(cf.ByAutomationId("LayoutEditor")) ?? desktop.FindFirstChild(cf.ByAutomationId("LayoutEditor"));
            AutomationElement OpenEditor()
            {
                In(main, "OpenLayoutEditor").AsButton().Invoke();
                return Retry.WhileNull(() => FindEditor(), TimeSpan.FromSeconds(10)).Result
                    ?? throw new InvalidOperationException("找不到 Layout 編輯器視窗");
            }

            // #86：模組只能從模組順序中選擇（預設的模組順序為 EXE、PLC、Server）
            var editor = OpenEditor();
            In(editor, "PlantName").AsTextBox().Text = "冒煙測試廠";
            In(editor, "LogSettingsTab").AsTabItem().Select();
            In(editor, "AddGapThreshold").AsButton().Invoke();
            var module = In(In(editor, "GapThresholds"), "GapThresholdModule").AsComboBox();
            Assert.Equal(["EXE", "PLC", "Server"], module.Items.Select(i => i.Text));
            module.Select("PLC");
            module.Collapse();
            In(editor, "SaveConfig").AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => In(editor, "EditorStatus").Name.Contains("修訂 1"), TimeSpan.FromSeconds(10)).Result,
                In(editor, "EditorStatus").Name);
            Assert.Contains("\"PLC\": 300", File.ReadAllText(Path.Combine(dataRoot, "Plants", "冒煙測試廠.json")));
            editor.AsWindow().Close();
            Assert.True(Retry.WhileFalse(() => FindEditor() is null, TimeSpan.FromSeconds(10)).Result);

            editor = OpenEditor();
            var config = Retry.WhileNull(() => In(editor, "ConfigList").FindFirstChild(cf.ByControlType(ControlType.ListItem)), TimeSpan.FromSeconds(10)).Result;
            Assert.NotNull(config);
            config.AsListBoxItem().Select();
            In(editor, "OpenSelectedConfig").AsButton().Invoke();
            In(editor, "LogSettingsTab").AsTabItem().Select();
            module = In(In(editor, "GapThresholds"), "GapThresholdModule").AsComboBox();
            Assert.True(Retry.WhileFalse(() => module.SelectedItem?.Text == "PLC", TimeSpan.FromSeconds(5)).Result, module.SelectedItem?.Text);
            editor.AsWindow().Close();
        }
        finally
        {
            app.Close();
            if (!app.HasExited) app.Kill();
        }
    }
}
