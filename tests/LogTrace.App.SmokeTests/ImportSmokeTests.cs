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
}
