using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using LogTrace.Tests.Support;

namespace LogTrace.App.SmokeTests;

// 異常紀錄面板（#110，ADR-0016）。測試資料：tests/fixtures/playback-prototype 的 plant.json、LOG 與 plant.incidents（虛構）
[Trait("Category", "UI")]
public sealed class IncidentsSmokeTests : IDisposable
{
    readonly TempFolder _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void 查詢列出有效的紀錄並標示改過的欄位_從案件建立草稿列在待審核清單()
    {
        var dataRoot = Path.Combine(_tmp.Path, "AppData");
        var plants = Directory.CreateDirectory(Path.Combine(dataRoot, "Plants")).FullName;
        var fixture = TempFolder.Fixture("playback-prototype");
        File.Copy(Path.Combine(fixture, "plant.json"), Path.Combine(plants, "範例廠.json"));
        var folder = Directory.CreateDirectory(Path.Combine(plants, "範例廠.incidents")).FullName;
        foreach (var f in Directory.GetFiles(Path.Combine(fixture, "plant.incidents"))) File.Copy(f, Path.Combine(folder, Path.GetFileName(f)));

        using var app = FlaUI.Core.Application.Launch(new ProcessStartInfo(ImportSmokeTests.AppExe(), $"--data-root \"{dataRoot}\""));
        using var automation = new UIA3Automation();
        try
        {
            var main = app.GetMainWindow(automation, TimeSpan.FromSeconds(30))!;
            var cf = main.ConditionFactory;
            AutomationElement In(string id) =>
                Retry.WhileNull(() => main.FindFirstDescendant(cf.ByAutomationId(id)), TimeSpan.FromSeconds(10)).Result
                ?? throw new InvalidOperationException($"找不到 {id}");
            AutomationElement[] Items(string id) =>
                In(id).FindAllChildren(cf.ByControlType(ControlType.DataItem).Or(cf.ByControlType(ControlType.ListItem)));

            In("PlantChoice").AsComboBox().Select("範例廠");
            In("NewCaseName").AsTextBox().Text = "虛構案件";
            In("CreateCase").AsButton().Invoke();
            In("ImportSource").AsTextBox().Text = Path.Combine(fixture, "logs");
            In("Import").AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => In("Status").Name.Contains("筆事件"), TimeSpan.FromSeconds(30)).Result);

            // 異常紀錄是獨立的窗格（只有一個面板時沒有分頁標題），一直顯示
            Assert.True(Retry.WhileFalse(() => Items("IncidentResults").Length == 1, TimeSpan.FromSeconds(10)).Result, In("IncidentStatus").Name);
            Items("IncidentResults")[0].AsListBoxItem().Select();
            Assert.True(Retry.WhileFalse(() => In("IncidentDetail").FindFirstDescendant(cf.ByName("★ 原因：減速機潤滑不足造成行走馬達過載（虛構）")) is not null,
                TimeSpan.FromSeconds(10)).Result);

            // 跳到事件
            Items("IncidentEvents")[0].AsListBoxItem().Select();
            In("IncidentGoToEvent").AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => ImportSmokeTests.DocumentText(In("EventDetail")).Contains("警報發生 SSCA01 E101"),
                TimeSpan.FromSeconds(10)).Result);

            // 從目前案件建立草稿：列在待審核清單，查詢仍只有一筆
            In("IncidentNewFromCase").AsButton().Invoke();
            In("IncidentQueueTab").AsTabItem().Select();
            Assert.True(Retry.WhileFalse(() => Items("IncidentQueue").Length == 1, TimeSpan.FromSeconds(10)).Result);
            Assert.Equal(2, Directory.GetFiles(folder, "*.json").Length);
            In("IncidentSearchTab").AsTabItem().Select();
            Assert.Single(Items("IncidentResults"));
        }
        finally
        {
            app.Close();
        }
    }
}
