using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using LogTrace.Tests.Support;

namespace LogTrace.App.SmokeTests;

// 廠別設定的新版提示與套用（#27）。測試資料：tests/fixtures/playback-prototype（虛構）
[Trait("Category", "UI")]
public sealed class PlantSnapshotSmokeTests : IDisposable
{
    readonly TempFolder _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void 廠別設定有新版時_重新開啟案件出現提示_套用後更新()
    {
        var dataRoot = Path.Combine(_tmp.Path, "AppData");
        var plantPath = Path.Combine(dataRoot, "Plants", "範例廠.json");
        Directory.CreateDirectory(Path.GetDirectoryName(plantPath)!);
        File.Copy(Path.Combine(TempFolder.Fixture("playback-prototype"), "plant.json"), plantPath);

        using var app = FlaUI.Core.Application.Launch(new ProcessStartInfo(ImportSmokeTests.AppExe(), $"--data-root \"{dataRoot}\""));
        using var automation = new UIA3Automation();
        try
        {
            var main = app.GetMainWindow(automation, TimeSpan.FromSeconds(30))!;
            var cf = main.ConditionFactory;
            AutomationElement In(string id) =>
                Retry.WhileNull(() => main.FindFirstDescendant(cf.ByAutomationId(id)), TimeSpan.FromSeconds(10)).Result
                ?? throw new InvalidOperationException($"找不到 {id}");

            In("PlantChoice").AsComboBox().Select("範例廠");
            In("NewCaseName").AsTextBox().Text = "快照案件";
            In("CreateCase").AsButton().Invoke();
            In("ImportSource").AsTextBox().Text = Path.Combine(TempFolder.Fixture("playback-prototype"), "logs");
            In("Import").AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => In("Status").Name.Contains("筆事件"), TimeSpan.FromSeconds(30)).Result);
            Assert.Null(main.FindFirstDescendant(cf.ByAutomationId("NewerPlantNotice")));

            // 在程式外修改廠別設定（虛構）：修訂號加 1
            File.WriteAllText(plantPath, File.ReadAllText(plantPath).Replace("\"revision\": 1,", "\"revision\": 2,"));

            In("CloseCase").AsButton().Invoke();
            var item = Retry.WhileNull(() => In("CaseList").FindFirstChild(cf.ByControlType(ControlType.ListItem)), TimeSpan.FromSeconds(10)).Result!;
            item.AsListBoxItem().Select();
            In("OpenSelectedCase").AsButton().Invoke();

            Assert.True(Retry.WhileFalse(() => In("NewerPlantNotice").Name.Contains("修訂 1 → 2"), TimeSpan.FromSeconds(30)).Result);
            In("ApplyNewerPlant").AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => In("Status").Name.Contains("已套用廠別設定修訂 2"), TimeSpan.FromSeconds(30)).Result, In("Status").Name);
            Assert.Contains("修訂 2", In("CurrentCasePlant").Name);
        }
        finally
        {
            app.Close();
            if (!app.HasExited) app.Kill();
        }
    }
}
