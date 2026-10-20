using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using LogTrace.Tests.Support;

namespace LogTrace.App.SmokeTests;

// 知識庫第一版（#80，ADR-0013 第 2 點）。測試資料：tests/fixtures/playback-prototype 的 plant.json 與 plant.kb.csv（虛構）
[Trait("Category", "UI")]
public sealed class KnowledgeSmokeTests : IDisposable
{
    readonly TempFolder _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void 知識庫面板列出並搜尋_詳細資訊顯示警報代碼的說明()
    {
        var dataRoot = Path.Combine(_tmp.Path, "AppData");
        var plants = Directory.CreateDirectory(Path.Combine(dataRoot, "Plants")).FullName;
        var fixture = TempFolder.Fixture("playback-prototype");
        File.Copy(Path.Combine(fixture, "plant.json"), Path.Combine(plants, "範例廠.json"));
        File.Copy(Path.Combine(fixture, "plant.kb.csv"), Path.Combine(plants, "範例廠.kb.csv"));

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
            In("NewCaseName").AsTextBox().Text = "知識庫案件";
            In("CreateCase").AsButton().Invoke();
            In("ImportSource").AsTextBox().Text = Path.Combine(fixture, "logs");
            In("Import").AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => In("Status").Name.Contains("筆事件"), TimeSpan.FromSeconds(30)).Result);

            ImportSmokeTests.OpenTab(main, "知識庫");
            Assert.True(Retry.WhileFalse(() => Items("KnowledgeResults").Length == 3, TimeSpan.FromSeconds(10)).Result);
            In("KnowledgeQuery").AsTextBox().Text = "SOP";
            In("SearchKnowledge").AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => Items("KnowledgeResults").Length == 1, TimeSpan.FromSeconds(10)).Result);
            Assert.Contains("符合 1 筆", In("KnowledgeStatus").Name);

            // 搜尋警報事件，選第一筆：詳細資訊顯示知識庫的說明
            ImportSmokeTests.OpenTab(main, "搜尋");
            In("SearchAllOf").AsTextBox().Text = "警報發生 E101";
            In("Search").AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => In("SearchStatus").Name.Contains("符合 1 筆"), TimeSpan.FromSeconds(10)).Result);
            Items("EventList")[0].AsListBoxItem().Select();
            Assert.True(Retry.WhileFalse(() => ImportSmokeTests.DocumentText(In("EventDetail")).Contains("說明：搬送機行走馬達過載（虛構）"),
                TimeSpan.FromSeconds(10)).Result, ImportSmokeTests.DocumentText(In("EventDetail")));
        }
        finally
        {
            app.Close();
            if (!app.HasExited) app.Kill();
        }
    }
}
