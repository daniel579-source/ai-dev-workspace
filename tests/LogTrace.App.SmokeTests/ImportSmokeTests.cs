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

    /// <summary>唯讀 RichTextBox（詳細資訊）的文字：以 UI Automation 的 TextPattern 讀取。</summary>
    internal static string DocumentText(AutomationElement element) =>
        Retry.WhileNull(() => element.Patterns.Text.PatternOrDefault?.DocumentRange.GetText(-1) is { Length: > 0 } t ? t : null, TimeSpan.FromSeconds(5)).Result ?? "";

    /// <summary>選取 AvalonDock 窗格中的分頁（例如「搜尋」）。</summary>
    internal static void OpenTab(AutomationElement window, string title)
    {
        // AvalonDock 的分頁標題在 UI Automation 中是 AutomationId 為 title 的文字
        var cf = window.ConditionFactory;
        var tab = Retry.WhileNull(() => window.FindFirstDescendant(cf.ByAutomationId("title").And(cf.ByName(title))), TimeSpan.FromSeconds(10)).Result
            ?? throw new InvalidOperationException($"找不到分頁 {title}");
        // 優先以 SelectionItem 選取所在的分頁（點標題文字不一定會切換）
        for (var e = tab.Parent; e is not null; e = e.Parent)
            if (e.Patterns.SelectionItem.PatternOrDefault is { } item)
            {
                item.Select();
                return;
            }
        tab.Click();
    }

    /// <summary>
    /// 受測的程式：預設是建置輸出；設定 LOGTRACE_APP_EXE 時改測發佈版（例如 zip 解壓後的 LogTrace.exe，#29）。
    /// </summary>
    internal static string AppExe()
    {
        if (Environment.GetEnvironmentVariable("LOGTRACE_APP_EXE") is { Length: > 0 } published)
        {
            Assert.True(File.Exists(published), $"找不到 {published}");
            return published;
        }
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
        var dataRoot = Path.Combine(_tmp.Path, "AppData");
        using var app = FlaUI.Core.Application.Launch(new ProcessStartInfo(AppExe(), $"--data-root \"{dataRoot}\""));
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
            Find("ImportSource").AsTextBox().Text = TempFolder.Fixture("one-day-plc");
            Find("Import").AsButton().Invoke();

            var list = Find("EventList");
            var rows = Retry.WhileFalse(
                () => list.FindAllChildren(cf.ByControlType(ControlType.DataItem).Or(cf.ByControlType(ControlType.ListItem))).Length == 5,
                TimeSpan.FromSeconds(30));
            Assert.True(rows.Result, "事件清單沒有出現 5 筆事件");
            Assert.Contains("案件共 5 筆事件", Find("Status").Name);
            Assert.Contains("PLCLOG.txt 第 5 行", DocumentText(Find("EventDetail")));
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
        var dataRoot = Path.Combine(_tmp.Path, "AppData");
        using var app = FlaUI.Core.Application.Launch(new ProcessStartInfo(AppExe(), $"--data-root \"{dataRoot}\""));
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
            Find("ImportSource").AsTextBox().Text = TempFolder.Fixture("parsing-cases");
            Find("Import").AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => Find("Status").Name.Contains("案件共 9 筆事件"), TimeSpan.FromSeconds(30)).Result);

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
            Assert.Contains("PLCLOG.txt 第 5～6 行", DocumentText(Find("EventDetail")));
        }
        finally
        {
            app.Close();
            if (!app.HasExited) app.Kill();
        }
    }

    [Fact]
    public void 匯入7z_關閉案件後從案件清單重新開啟()
    {
        var dataRoot = Path.Combine(_tmp.Path, "AppData");
        using var app = FlaUI.Core.Application.Launch(new ProcessStartInfo(AppExe(), $"--data-root \"{dataRoot}\""));
        using var automation = new UIA3Automation();
        try
        {
            var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(30));
            Assert.NotNull(window);
            var cf = window.ConditionFactory;
            AutomationElement Find(string id) =>
                Retry.WhileNull(() => window.FindFirstDescendant(cf.ByAutomationId(id)), TimeSpan.FromSeconds(10)).Result
                ?? throw new InvalidOperationException($"找不到 {id}");
            AutomationElement[] Rows() =>
                Find("EventList").FindAllChildren(cf.ByControlType(ControlType.DataItem).Or(cf.ByControlType(ControlType.ListItem)));

            Assert.Equal(Path.Combine(dataRoot, "Cases"), Find("CasesRoot").Name);
            Find("NewCaseName").AsTextBox().Text = "壓縮檔案件";
            Find("CreateCase").AsButton().Invoke();
            Find("ImportSource").AsTextBox().Text = TempFolder.Fixture("multi-day.7z");
            Find("Import").AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => Find("Status").Name.Contains("案件共 13 筆事件"), TimeSpan.FromSeconds(30)).Result, Find("Status").Name);

            Find("CloseCase").AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => Rows().Length == 0, TimeSpan.FromSeconds(10)).Result);

            var item = Retry.WhileNull(() => Find("CaseList").FindFirstChild(cf.ByControlType(ControlType.ListItem)), TimeSpan.FromSeconds(10)).Result;
            Assert.NotNull(item);
            item.AsListBoxItem().Select();
            Find("OpenSelectedCase").AsButton().Invoke();

            // 清單是虛擬化的，只會產生看得到的列；以狀態列確認事件數
            Assert.True(Retry.WhileFalse(() => Find("Status").Name.Contains("13 筆事件") && Rows().Length > 0, TimeSpan.FromSeconds(30)).Result,
                Find("Status").Name);
            Assert.Contains("壓縮檔案件", Find("CurrentCase").Name);
        }
        finally
        {
            app.Close();
            if (!app.HasExited) app.Kill();
        }
    }
}
