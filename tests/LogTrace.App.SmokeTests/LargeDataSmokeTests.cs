using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using LogTrace.Tests.Support;
using Xunit.Abstractions;

namespace LogTrace.App.SmokeTests;

/// <summary>
/// 以大量虛構資料在畫面上走一次主要流程（#29）：匯入 → 搜尋 → 點結果 → 方向鍵，記錄各步驟的時間。
/// 資料量大，不放在 repo：設定 LOGTRACE_E2E_SOURCE（例如技術驗證產生的 tv-data\logs\20300107）後才執行。
/// 可以搭配 LOGTRACE_APP_EXE 測試發佈版。
/// </summary>
[Trait("Category", "UI")]
public sealed class LargeDataSmokeTests(ITestOutputHelper output) : IDisposable
{
    readonly TempFolder _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void 一天份資料_匯入_搜尋_點結果_方向鍵()
    {
        var source = Environment.GetEnvironmentVariable("LOGTRACE_E2E_SOURCE");
        if (string.IsNullOrEmpty(source))
        {
            output.WriteLine("未設定 LOGTRACE_E2E_SOURCE，略過。");
            return;
        }
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
            AutomationElement[] Rows() =>
                In("EventList").FindAllChildren(cf.ByControlType(ControlType.DataItem).Or(cf.ByControlType(ControlType.ListItem)));
            void ClickRow(AutomationElement row) =>
                Mouse.Click(new System.Drawing.Point((int)row.BoundingRectangle.Left + 40, (int)row.BoundingRectangle.Top + (int)row.BoundingRectangle.Height / 2));
            TimeSpan Measure(Func<bool> done, TimeSpan timeout)
            {
                var w = Stopwatch.StartNew();
                Assert.True(Retry.WhileFalse(done, timeout, TimeSpan.FromMilliseconds(100)).Result, "逾時");
                return w.Elapsed;
            }

            In("NewCaseName").AsTextBox().Text = "一天份";
            In("CreateCase").AsButton().Invoke();
            In("ImportSource").AsTextBox().Text = source;
            In("Import").AsButton().Invoke();
            var import = Measure(() => In("Status").Name.Contains("筆事件"), TimeSpan.FromMinutes(5));
            output.WriteLine($"匯入到可以瀏覽：{import.TotalSeconds:N1} 秒（{In("Status").Name}）");

            ImportSmokeTests.OpenTab(main, "搜尋");
            In("SearchAllOf").AsTextBox().Text = "00010927"; // 虛構的命令編號
            In("Search").AsButton().Invoke();
            var search = Measure(() => In("SearchStatus").Name.StartsWith("符合"), TimeSpan.FromSeconds(30));
            output.WriteLine($"搜尋（索引建立中）：{search.TotalSeconds:N1} 秒（{In("SearchStatus").Name}）");

            Assert.True(Retry.WhileFalse(() => Rows().Length > 1, TimeSpan.FromSeconds(10)).Result);
            var before = In("CurrentTime").Name;
            ClickRow(Rows()[1]);
            var click = Measure(() => In("CurrentTime").Name != before, TimeSpan.FromSeconds(10));
            output.WriteLine($"點結果到所有面板同步：{click.TotalSeconds:N2} 秒（{In("CurrentTime").Name}）");

            In("ShowAllWithMarks").AsRadioButton().Click();
            ClickRow(Rows().First(r => r.AsListBoxItem().IsSelected));
            var steps = new List<double>();
            for (int i = 0; i < 5; i++)
            {
                var t = In("CurrentTime").Name;
                Keyboard.Type(VirtualKeyShort.RIGHT);
                steps.Add(Measure(() => In("CurrentTime").Name != t, TimeSpan.FromSeconds(10)).TotalSeconds);
            }
            output.WriteLine($"方向鍵 → 5 次：{string.Join("、", steps.Select(s => $"{s:N2}"))} 秒");

            var indexed = Measure(() => In("IndexStatus").Name.Contains("已建立"), TimeSpan.FromMinutes(5));
            output.WriteLine($"全文索引：{In("IndexStatus").Name}（再等 {indexed.TotalSeconds:N1} 秒）");
            In("Search").AsButton().Invoke();
            Thread.Sleep(200);
            Measure(() => In("SearchStatus").Name.StartsWith("符合"), TimeSpan.FromSeconds(30));
            output.WriteLine($"搜尋（索引建好後）：{In("SearchStatus").Name}");
        }
        finally
        {
            app.Close();
            if (!app.HasExited) app.Kill();
        }
    }

    /// <summary>
    /// 需要等待時表示程式正在運作：匯入期間狀態列有忙碌表示；
    /// 符合十幾萬筆（一天份「CMD_NO」）時畫面執行緒會被佔住一段時間，這時另開「處理中」視窗，恢復後自動關閉。
    /// </summary>
    [Fact]
    public void 一天份資料_需要等待時表示程式正在運作()
    {
        var source = Environment.GetEnvironmentVariable("LOGTRACE_E2E_SOURCE");
        if (string.IsNullOrEmpty(source))
        {
            output.WriteLine("未設定 LOGTRACE_E2E_SOURCE，略過。");
            return;
        }
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
            // 主視窗沒有回應時，用 UI Automation 在桌面上找視窗會被擋住或丟出例外，所以直接依標題找（不經過主視窗）
            IntPtr BusyWindow()
            {
                var hwnd = NativeMethods.FindWindowW(null, "LogTrace 處理中");
                return hwnd != IntPtr.Zero && NativeMethods.IsWindowVisible(hwnd) ? hwnd : IntPtr.Zero;
            }
            bool BusyWindowGone() => BusyWindow() == IntPtr.Zero;

            In("NewCaseName").AsTextBox().Text = "一天份";
            In("CreateCase").AsButton().Invoke();
            Assert.Null(main.FindFirstDescendant(cf.ByAutomationId("BusyIndicator"))); // 沒有工作時不顯示
            In("ImportSource").AsTextBox().Text = source;
            In("Import").AsButton().Invoke();
            Assert.StartsWith("已經過", In("BusyElapsed").Name);
            Assert.True(Retry.WhileFalse(() => In("Status").Name.Contains("筆事件"), TimeSpan.FromMinutes(5), TimeSpan.FromMilliseconds(200)).Result);
            Assert.True(Retry.WhileFalse(() => main.FindFirstDescendant(cf.ByAutomationId("BusyIndicator")) is null, TimeSpan.FromSeconds(30)).Result);
            Assert.True(Retry.WhileFalse(BusyWindowGone, TimeSpan.FromSeconds(30)).Result);

            ImportSmokeTests.OpenTab(main, "搜尋");
            In("SearchAllOf").AsTextBox().Text = "CMD_NO";
            var clock = Stopwatch.StartNew();
            In("Search").AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => !BusyWindowGone(), TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(100)).Result, "處理中視窗沒有出現");
            output.WriteLine($"處理中視窗出現：{clock.Elapsed.TotalSeconds:N1} 秒");
            // 處理中視窗有自己的畫面執行緒，主視窗沒有回應時仍然可以讀取
            var shown = automation.FromHandle(BusyWindow());
            Assert.Equal("BusyWindow", shown.AutomationId);
            var texts = shown.FindAllDescendants(cf.ByControlType(ControlType.Text)).Select(t => t.Name).ToList();
            Assert.Contains(texts, t => t.Contains("沒有當機"));
            Assert.Contains(texts, t => t.StartsWith("已經過"));

            Assert.True(Retry.WhileFalse(BusyWindowGone, TimeSpan.FromMinutes(10), TimeSpan.FromMilliseconds(500)).Result, "處理中視窗沒有關閉");
            output.WriteLine($"處理中視窗關閉：{clock.Elapsed.TotalSeconds:N1} 秒（{In("SearchStatus").Name}）");
            Assert.StartsWith("符合", In("SearchStatus").Name);
        }
        finally
        {
            app.Close();
            if (!app.HasExited) app.Kill();
        }
    }

    static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern IntPtr FindWindowW(string? className, string windowName);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr hWnd);
    }
}
