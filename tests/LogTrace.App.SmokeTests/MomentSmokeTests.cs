using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using LogTrace.Tests.Support;

namespace LogTrace.App.SmokeTests;

// 目前時刻與方向鍵（#25）。測試資料：tests/fixtures/playback-prototype（虛構）
[Trait("Category", "UI")]
public sealed class MomentSmokeTests : IDisposable
{
    readonly TempFolder _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void 搜尋_點結果_方向鍵_Layout與事件清單同步()
    {
        var dataRoot = Path.Combine(_tmp.Path, "AppData");
        Directory.CreateDirectory(Path.Combine(dataRoot, "Plants"));
        File.Copy(Path.Combine(TempFolder.Fixture("playback-prototype"), "plant.json"), Path.Combine(dataRoot, "Plants", "範例廠.json"));

        using var app = FlaUI.Core.Application.Launch(new ProcessStartInfo(ImportSmokeTests.AppExe(), $"--data-root \"{dataRoot}\""));
        using var automation = new UIA3Automation();
        try
        {
            var main = app.GetMainWindow(automation, TimeSpan.FromSeconds(30))!;
            var cf = main.ConditionFactory;
            AutomationElement In(string id) =>
                Retry.WhileNull(() => main.FindFirstDescendant(cf.ByAutomationId(id)), TimeSpan.FromSeconds(10)).Result
                ?? throw new InvalidOperationException($"找不到 {id}");
            // 事件清單的一列比可見範圍寬（欄位很多），點中央會點到別的面板：改點左側
            void ClickRow(AutomationElement row) =>
                Mouse.Click(new System.Drawing.Point((int)row.BoundingRectangle.Left + 40, (int)row.BoundingRectangle.Top + (int)row.BoundingRectangle.Height / 2));
            AutomationElement[] Rows() =>
                In("EventList").FindAllChildren(cf.ByControlType(ControlType.DataItem).Or(cf.ByControlType(ControlType.ListItem)));

            In("PlantChoice").AsComboBox().Select("範例廠");
            In("NewCaseName").AsTextBox().Text = "時刻案件";
            In("CreateCase").AsButton().Invoke();
            In("ImportSource").AsTextBox().Text = Path.Combine(TempFolder.Fixture("playback-prototype"), "logs");
            In("Import").AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => In("Status").Name.Contains("筆事件"), TimeSpan.FromSeconds(30)).Result);

            // 搜尋，顯示全部並標示符合的
            ImportSmokeTests.OpenTab(main, "搜尋");
            In("SearchAllOf").AsTextBox().Text = "SSCA01";
            In("Search").AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => In("SearchStatus").Name.StartsWith("符合"), TimeSpan.FromSeconds(10)).Result);

            // 點第二筆結果（00040001 第1段 抵達 1003，10:00:40），再點第一筆（派送 00040001 第1段，10:00:10）
            Assert.True(Retry.WhileFalse(() => Rows().Length > 1, TimeSpan.FromSeconds(10)).Result);
            ClickRow(Rows()[1]);
            Assert.True(Retry.WhileFalse(() => In("CurrentTime").Name.Contains("10:00:40.0000"), TimeSpan.FromSeconds(10)).Result, In("CurrentTime").Name);
            ClickRow(Rows()[0]);
            Assert.True(Retry.WhileFalse(() => In("CurrentTime").Name.Contains("10:00:10.0000"), TimeSpan.FromSeconds(10)).Result, In("CurrentTime").Name);
            var momentBefore = In("PlaybackMoment").Name;

            // 回到全部事件，按 →：下一個狀態變化（10:00:11 A-01 在席 OFF）
            In("ShowAllWithMarks").AsRadioButton().Click();
            ClickRow(Rows().First(r => r.AsListBoxItem().IsSelected));
            Keyboard.Type(VirtualKeyShort.RIGHT);

            Assert.True(Retry.WhileFalse(() => In("CurrentTime").Name.Contains("10:00:11.0000"), TimeSpan.FromSeconds(10)).Result, In("CurrentTime").Name);
            Assert.NotEqual(momentBefore, In("PlaybackMoment").Name);
            Assert.Contains("10:00:11.0000", In("PlaybackMoment").Name); // Layout 同步
            var selected = Rows().Single(r => r.AsListBoxItem().IsSelected);
            Assert.Contains("10:00:11.0000", string.Concat(selected.FindAllDescendants().Select(e => e.Name))); // 事件清單同步

            if (Environment.GetEnvironmentVariable("SHOT") is { } shot) FlaUI.Core.Capturing.Capture.Element(main).ToFile(shot);
        }
        finally
        {
            app.Close();
            if (!app.HasExited) app.Kill();
        }
    }
}
