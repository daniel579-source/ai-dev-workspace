using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using LogTrace.Tests.Support;

namespace LogTrace.App.SmokeTests;

// 全文索引的狀態與正規表示式（#21）。測試資料：tests/fixtures/search-cases（虛構）
[Trait("Category", "UI")]
public sealed class SearchIndexSmokeTests : IDisposable
{
    readonly TempFolder _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void 顯示索引狀態_正規表示式搜尋_語法有誤時提示()
    {
        var dataRoot = Path.Combine(_tmp.Path, "AppData");
        using var app = FlaUI.Core.Application.Launch(new ProcessStartInfo(ImportSmokeTests.AppExe(), $"--data-root \"{dataRoot}\""));
        using var automation = new UIA3Automation();
        try
        {
            var main = app.GetMainWindow(automation, TimeSpan.FromSeconds(30))!;
            var cf = main.ConditionFactory;
            AutomationElement In(string id) =>
                Retry.WhileNull(() => main.FindFirstDescendant(cf.ByAutomationId(id)), TimeSpan.FromSeconds(10)).Result
                ?? throw new InvalidOperationException($"找不到 {id}");

            In("NewCaseName").AsTextBox().Text = "索引";
            In("CreateCase").AsButton().Invoke();
            In("ImportSource").AsTextBox().Text = TempFolder.Fixture("search-cases");
            In("Import").AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => In("Status").Name.Contains("筆事件"), TimeSpan.FromSeconds(30)).Result);

            ImportSmokeTests.OpenTab(main, "搜尋");
            // 測試資料很小，案件早已超過原始 LOG 的 5 倍：不建索引，說明以掃描搜尋
            Assert.True(Retry.WhileFalse(() => In("IndexStatus").Name.Contains("其餘以掃描搜尋"), TimeSpan.FromSeconds(30)).Result, In("IndexStatus").Name);

            In("SearchRegex").AsCheckBox().IsChecked = true;
            In("SearchAllOf").AsTextBox().Text = @"bcr\d1";
            In("Search").AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => In("SearchStatus").Name.StartsWith("符合 3 筆"), TimeSpan.FromSeconds(10)).Result, In("SearchStatus").Name);

            In("SearchAllOf").AsTextBox().Text = "bcr(01";
            In("Search").AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => In("SearchStatus").Name.StartsWith("正規表示式「bcr(01」有誤"), TimeSpan.FromSeconds(10)).Result, In("SearchStatus").Name);
        }
        finally
        {
            app.Close();
            if (!app.HasExited) app.Kill();
        }
    }
}
