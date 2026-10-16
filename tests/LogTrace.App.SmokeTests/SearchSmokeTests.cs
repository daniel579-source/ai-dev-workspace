using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using LogTrace.Tests.Support;

namespace LogTrace.App.SmokeTests;

// 搜尋（#20）。測試資料：tests/fixtures/search-cases（虛構）
[Trait("Category", "UI")]
public sealed class SearchSmokeTests : IDisposable
{
    readonly TempFolder _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void 搜尋關鍵字_顯示符合筆數_電話號碼遮罩後可以顯示完整號碼()
    {
        using var app = FlaUI.Core.Application.Launch(new ProcessStartInfo(ImportSmokeTests.AppExe(), $"--data-root \"{Path.Combine(_tmp.Path, "AppData")}\""));
        using var automation = new UIA3Automation();
        try
        {
            var main = app.GetMainWindow(automation, TimeSpan.FromSeconds(30))!;
            var cf = main.ConditionFactory;
            AutomationElement In(string id) =>
                Retry.WhileNull(() => main.FindFirstDescendant(cf.ByAutomationId(id)), TimeSpan.FromSeconds(10)).Result
                ?? throw new InvalidOperationException($"找不到 {id}");

            In("NewCaseName").AsTextBox().Text = "搜尋案件";
            In("CreateCase").AsButton().Invoke();
            In("ImportSource").AsTextBox().Text = TempFolder.Fixture("search-cases");
            In("Import").AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => In("Status").Name.Contains("案件共 12 筆事件"), TimeSpan.FromSeconds(30)).Result, In("Status").Name);

            ImportSmokeTests.OpenTab(main, "搜尋");
            In("SearchAllOf").AsTextBox().Text = "卸貨";
            In("Search").AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => In("SearchStatus").Name.StartsWith("符合 4 筆"), TimeSpan.FromSeconds(10)).Result, In("SearchStatus").Name);

            In("SearchAllOf").AsTextBox().Text = "0912-345-678";
            In("Search").AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => In("SearchStatus").Name.StartsWith("符合 1 筆"), TimeSpan.FromSeconds(10)).Result, In("SearchStatus").Name);
            Assert.True(Retry.WhileFalse(() => ImportSmokeTests.DocumentText(In("EventDetail")).Contains("09xx-xxx-xxx"), TimeSpan.FromSeconds(10)).Result);
            Assert.DoesNotContain("0912-345-678", ImportSmokeTests.DocumentText(In("EventDetail")));

            In("ToggleReveal").AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => ImportSmokeTests.DocumentText(In("EventDetail")).Contains("0912-345-678"), TimeSpan.FromSeconds(10)).Result);

            if (Environment.GetEnvironmentVariable("SHOT") is { } shot) FlaUI.Core.Capturing.Capture.Element(main).ToFile(shot);
        }
        finally
        {
            app.Close();
            if (!app.HasExited) app.Kill();
        }
    }
}
