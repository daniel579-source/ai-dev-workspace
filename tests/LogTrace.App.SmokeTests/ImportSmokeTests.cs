using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using LogTrace.Tests.Support;

namespace LogTrace.App.SmokeTests;

[Trait("Category", "UI")]
public sealed class ImportSmokeTests : IDisposable
{
    readonly TempFolder _tmp = new();

    public void Dispose() => _tmp.Dispose();

    static string AppExe()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LogTrace.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
#if DEBUG
        const string config = "Debug";
#else
        const string config = "Release";
#endif
        var exe = Path.Combine(dir!.FullName, "src", "LogTrace.App", "bin", config, "net10.0-windows", "LogTrace.exe");
        Assert.True(File.Exists(exe), $"找不到 {exe}");
        return exe;
    }

    [Fact]
    public void 啟動程式_建立案件_匯入_看到事件清單()
    {
        var casesRoot = Path.Combine(_tmp.Path, "Cases");
        using var app = FlaUI.Core.Application.Launch(new ProcessStartInfo(AppExe(), $"--cases-root \"{casesRoot}\""));
        using var automation = new UIA3Automation();
        try
        {
            var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(30));
            Assert.NotNull(window);
            var cf = window.ConditionFactory;
            AutomationElement Find(string id) =>
                Retry.WhileNull(() => window.FindFirstDescendant(cf.ByAutomationId(id)), TimeSpan.FromSeconds(10)).Result
                ?? throw new InvalidOperationException($"找不到 {id}");

            Find("NewCaseName").AsTextBox().Text = "冒煙測試";
            Find("CreateCase").AsButton().Invoke();
            Find("ImportFolder").AsTextBox().Text = TempFolder.Fixture("one-day-plc");
            Find("Import").AsButton().Invoke();

            var list = Find("EventList");
            var rows = Retry.WhileFalse(
                () => list.FindAllChildren(cf.ByControlType(ControlType.DataItem).Or(cf.ByControlType(ControlType.ListItem))).Length == 5,
                TimeSpan.FromSeconds(30));
            Assert.True(rows.Result, "事件清單沒有出現 5 筆事件");
            Assert.Contains("已匯入 1 個檔案、5 筆事件", Find("Status").Name);
            Assert.Contains("PLCLOG.txt 第 5 行", Find("EventDetail").AsTextBox().Text);
        }
        finally
        {
            app.Close();
            if (!app.HasExited) app.Kill();
        }
    }

    [Fact]
    public void 選取事件時_原始LOG面板跳到對應的行_切換分軌檢視後選取不變()
    {
        var casesRoot = Path.Combine(_tmp.Path, "Cases");
        using var app = FlaUI.Core.Application.Launch(new ProcessStartInfo(AppExe(), $"--cases-root \"{casesRoot}\""));
        using var automation = new UIA3Automation();
        try
        {
            var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(30));
            Assert.NotNull(window);
            var cf = window.ConditionFactory;
            AutomationElement Find(string id) =>
                Retry.WhileNull(() => window.FindFirstDescendant(cf.ByAutomationId(id)), TimeSpan.FromSeconds(10)).Result
                ?? throw new InvalidOperationException($"找不到 {id}");

            Find("NewCaseName").AsTextBox().Text = "冒煙測試二";
            Find("CreateCase").AsButton().Invoke();
            Find("ImportFolder").AsTextBox().Text = TempFolder.Fixture("parsing-cases");
            Find("Import").AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => Find("Status").Name.Contains("9 筆事件"), TimeSpan.FromSeconds(30)).Result);

            // 圖例列出五種旗標
            Assert.NotNull(Find("FlagLegend").FindFirstDescendant(cf.ByName("編碼錯誤")));

            var list = Find("EventList");
            var rows = list.FindAllChildren(cf.ByControlType(ControlType.DataItem).Or(cf.ByControlType(ControlType.ListItem)));
            Assert.Equal(9, rows.Length);
            rows[6].AsListBoxItem().Select(); // 第 7 筆：PLCLOG.txt 第 5～6 行（含延續行）

            Assert.True(Retry.WhileFalse(() => Find("RawFile").Name.EndsWith("PLCLOG.txt"), TimeSpan.FromSeconds(10)).Result);
            var rawSelected = Retry.WhileNull(() => Find("RawLog").AsListBox().SelectedItem, TimeSpan.FromSeconds(10)).Result;
            Assert.NotNull(rawSelected);
            Assert.Contains("[A-03][Unload]", rawSelected.FindAllDescendants().Select(e => e.Name).Aggregate("", string.Concat));

            Find("TrackView").AsRadioButton().Click();
            Assert.True(Retry.WhileNull(() => list.FindFirstDescendant(cf.ByControlType(ControlType.HeaderItem).And(cf.ByName("Server"))),
                TimeSpan.FromSeconds(10)).Result is not null, "分軌檢視沒有 Server 欄");
            Assert.True(list.FindAllChildren(cf.ByControlType(ControlType.DataItem).Or(cf.ByControlType(ControlType.ListItem)))[6]
                .AsListBoxItem().IsSelected);
            Assert.Contains("PLCLOG.txt 第 5～6 行", Find("EventDetail").AsTextBox().Text);
        }
        finally
        {
            app.Close();
            if (!app.HasExited) app.Kill();
        }
    }
}
