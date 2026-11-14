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
            // 3D 畫布載入完成，沒有網路請求（ADR-0018）
            Assert.True(Retry.WhileFalse(() => In(editor, "Editor3DStatus").Name.StartsWith("3D 已載入", StringComparison.Ordinal), TimeSpan.FromSeconds(30)).Result,
                In(editor, "Editor3DStatus").Name + " " + editor.FindFirstDescendant(cf.ByAutomationId("Editor3DError"))?.Name);
            Assert.DoesNotContain("已封鎖", In(editor, "Editor3DStatus").Name);

            // 點 3D 畫布上的元素名稱選取它，屬性面板顯示；再 Ctrl+C／Ctrl+V／Del／Ctrl+Z（按鍵由網頁轉給程式）
            int Count() => In(editor, "ElementList").FindAllChildren(cf.ByControlType(ControlType.ListItem)).Length;
            bool Becomes(int n) => Retry.WhileFalse(() => Count() == n, TimeSpan.FromSeconds(5)).Result;
            var label = Retry.WhileNull(() => In(editor, "Editor3D").FindFirstDescendant(cf.ByName("新站點 1")), TimeSpan.FromSeconds(20)).Result
                ?? throw new InvalidOperationException("3D 畫布中找不到「新站點 1」");
            label.Click();
            Assert.True(Retry.WhileFalse(() => In(editor, "ElementName").AsTextBox().Text == "新站點 1", TimeSpan.FromSeconds(10)).Result);
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
    public void 防火門_設定平時狀態與關聯輸送線後存檔()
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
            In(main, "OpenLayoutEditor").AsButton().Invoke();
            var editor = Retry.WhileNull(
                () => main.FindFirstChild(cf.ByAutomationId("LayoutEditor")) ?? desktop.FindFirstChild(cf.ByAutomationId("LayoutEditor")),
                TimeSpan.FromSeconds(10)).Result ?? throw new InvalidOperationException("找不到 Layout 編輯器視窗");

            In(editor, "PlantName").AsTextBox().Text = "防火門測試廠";
            In(editor, "Add_Station").AsButton().Invoke();
            In(editor, "Add_Station").AsButton().Invoke();
            In(editor, "Add_Conveyor").AsButton().Invoke();
            In(editor, "Add_FireDoor").AsButton().Invoke();
            In(editor, "NewAlias").AsTextBox().Text = "FD-01"; // 虛構的編號
            In(editor, "AddAlias").AsButton().Invoke();
            var state = In(editor, "DoorState").AsComboBox();
            state.Select("關");
            var conveyor = In(editor, "NewDoorConveyor").AsComboBox();
            conveyor.Select(0);
            In(editor, "AddDoorConveyor").AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => In(editor, "EditorStatus").Name.Contains("關聯到"), TimeSpan.FromSeconds(5)).Result,
                In(editor, "EditorStatus").Name);
            In(editor, "SaveConfig").AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => In(editor, "EditorStatus").Name.Contains("修訂 1"), TimeSpan.FromSeconds(10)).Result,
                In(editor, "EditorStatus").Name);

            var json = File.ReadAllText(Path.Combine(dataRoot, "Plants", "防火門測試廠.json"));
            Assert.Contains("\"formatVersion\": 3", json);
            Assert.Contains("\"type\": \"fireDoor\"", json);
            Assert.Contains("\"doorState\": \"closed\"", json);
            Assert.Contains("\"cv-1\"", json);
            editor.AsWindow().Close();
        }
        finally
        {
            app.Close();
            if (!app.HasExited) app.Kill();
        }
    }

    [Fact]
    public void 樓層_新增樓層與元素_只顯示目前樓層_存檔後寫出樓層()
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
            In(main, "OpenLayoutEditor").AsButton().Invoke();
            var editor = Retry.WhileNull(
                () => main.FindFirstChild(cf.ByAutomationId("LayoutEditor")) ?? desktop.FindFirstChild(cf.ByAutomationId("LayoutEditor")),
                TimeSpan.FromSeconds(10)).Result ?? throw new InvalidOperationException("找不到 Layout 編輯器視窗");

            In(editor, "PlantName").AsTextBox().Text = "樓層測試廠";
            In(editor, "Add_Station").AsButton().Invoke(); // 1F
            In(editor, "AddFloor").AsButton().Invoke();
            In(editor, "Add_Station").AsButton().Invoke(); // 2F
            Assert.Equal("2F", In(editor, "ElementFloor").AsComboBox().SelectedItem?.Text);
            // 3D 中兩層都畫出來（上下堆疊）；勾「只顯示目前樓層」後只剩 2F（ADR-0018）
            var canvas = In(editor, "Editor3D");
            Assert.True(Retry.WhileFalse(() => canvas.FindFirstDescendant(cf.ByName("新站點 1")) is not null && canvas.FindFirstDescendant(cf.ByName("新站點 2")) is not null,
                TimeSpan.FromSeconds(30)).Result);
            In(editor, "EditorOnlyCurrentFloor").AsCheckBox().IsChecked = true;
            Assert.True(Retry.WhileFalse(() => canvas.FindFirstDescendant(cf.ByName("新站點 1")) is null, TimeSpan.FromSeconds(10)).Result);
            Assert.NotNull(canvas.FindFirstDescendant(cf.ByName("新站點 2")));
            In(editor, "SaveConfig").AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => In(editor, "EditorStatus").Name.Contains("修訂 1"), TimeSpan.FromSeconds(10)).Result,
                In(editor, "EditorStatus").Name);

            var json = File.ReadAllText(Path.Combine(dataRoot, "Plants", "樓層測試廠.json"));
            Assert.Contains("\"floors\": [", json);
            Assert.Contains("\"floor\": \"2F\"", json);
            editor.AsWindow().Close();
        }
        finally
        {
            app.Close();
            if (!app.HasExited) app.Kill();
        }
    }

    [Fact]
    public void 導軌_新增跑道形導軌_插入點_搬送機指定導軌後存檔()
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
            In(main, "OpenLayoutEditor").AsButton().Invoke();
            var editor = Retry.WhileNull(
                () => main.FindFirstChild(cf.ByAutomationId("LayoutEditor")) ?? desktop.FindFirstChild(cf.ByAutomationId("LayoutEditor")),
                TimeSpan.FromSeconds(10)).Result ?? throw new InvalidOperationException("找不到 Layout 編輯器視窗");

            In(editor, "PlantName").AsTextBox().Text = "導軌測試廠";
            In(editor, "Add_RailLoop").AsButton().Invoke();
            Assert.True(In(editor, "RailClosed").AsCheckBox().IsChecked);
            In(editor, "RailSegment").AsComboBox().Select(0);
            In(editor, "InsertRailPoint").AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => In(editor, "EditorStatus").Name.Contains("插入一點"), TimeSpan.FromSeconds(5)).Result,
                In(editor, "EditorStatus").Name);

            In(editor, "Add_Vehicle").AsButton().Invoke();
            In(editor, "VehicleRail").AsComboBox().Select(0);
            In(editor, "SaveConfig").AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => In(editor, "EditorStatus").Name.Contains("修訂 1"), TimeSpan.FromSeconds(10)).Result,
                In(editor, "EditorStatus").Name);

            var json = File.ReadAllText(Path.Combine(dataRoot, "Plants", "導軌測試廠.json"));
            Assert.Contains("\"formatVersion\": 4", json);
            Assert.Contains("\"type\": \"rail\"", json);
            Assert.Contains("\"closed\": true", json);
            Assert.Contains("\"rail\": \"rl-1\"", json);
            editor.AsWindow().Close();
        }
        finally
        {
            app.Close();
            if (!app.HasExited) app.Kill();
        }
    }

    [Fact]
    public void 電梯_新增後服務所有樓層_取消一層_存檔寫出服務的樓層()
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
            In(main, "OpenLayoutEditor").AsButton().Invoke();
            var editor = Retry.WhileNull(
                () => main.FindFirstChild(cf.ByAutomationId("LayoutEditor")) ?? desktop.FindFirstChild(cf.ByAutomationId("LayoutEditor")),
                TimeSpan.FromSeconds(10)).Result ?? throw new InvalidOperationException("找不到 Layout 編輯器視窗");

            In(editor, "PlantName").AsTextBox().Text = "電梯測試廠";
            In(editor, "AddFloor").AsButton().Invoke(); // 2F
            In(editor, "AddFloor").AsButton().Invoke(); // 3F
            In(editor, "Add_Elevator").AsButton().Invoke();
            In(editor, "NewAlias").AsTextBox().Text = "L1"; // 虛構的編號
            In(editor, "AddAlias").AsButton().Invoke();
            var third = In(editor, "ServedFloor_3F").AsCheckBox();
            Assert.True(third.IsChecked);
            third.IsChecked = false;
            Assert.True(Retry.WhileFalse(() => In(editor, "EditorStatus").Name.Contains("服務 1F、2F"), TimeSpan.FromSeconds(5)).Result,
                In(editor, "EditorStatus").Name);
            In(editor, "SaveConfig").AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => In(editor, "EditorStatus").Name.Contains("修訂 1"), TimeSpan.FromSeconds(10)).Result,
                In(editor, "EditorStatus").Name);

            var json = File.ReadAllText(Path.Combine(dataRoot, "Plants", "電梯測試廠.json"));
            Assert.Contains("\"formatVersion\": 5", json);
            Assert.Contains("\"type\": \"elevator\"", json);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var elevator = doc.RootElement.GetProperty("layout").GetProperty("elements").EnumerateArray()
                .Single(e => e.GetProperty("type").GetString() == "elevator");
            Assert.Equal(["1F", "2F"], elevator.GetProperty("servedFloors").EnumerateArray().Select(f => f.GetString()!).ToArray());
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
