using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using LogTrace.Tests.Support;

namespace LogTrace.App.SmokeTests;

// Layout 回放（#24）。測試資料：tests/fixtures/playback-prototype（虛構）
[Trait("Category", "UI")]
public sealed class PlaybackSmokeTests : IDisposable
{
    readonly TempFolder _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void 開啟有Layout的案件_移到案件結尾_點站點顯示狀態()
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

            In("PlantChoice").AsComboBox().Select("範例廠");
            In("NewCaseName").AsTextBox().Text = "回放案件";
            In("CreateCase").AsButton().Invoke();
            In("ImportSource").AsTextBox().Text = Path.Combine(TempFolder.Fixture("playback-prototype"), "logs");
            In("Import").AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => In("Status").Name.Contains("筆事件"), TimeSpan.FromSeconds(30)).Result, In("Status").Name);
            Assert.True(Retry.WhileFalse(() => In("PlaybackMoment").Name.Contains("事件 #1"), TimeSpan.FromSeconds(10)).Result, In("PlaybackMoment").Name);

            // 移到案件結尾：事件清單按 End
            var first = In("EventList").FindFirstChild(cf.ByControlType(ControlType.DataItem).Or(cf.ByControlType(ControlType.ListItem)));
            Assert.NotNull(first);
            first.AsListBoxItem().Select();
            first.Focus();
            Keyboard.Type(VirtualKeyShort.END);
            Assert.True(Retry.WhileFalse(() => In("PlaybackMoment").Name.Contains("10:06:00"), TimeSpan.FromSeconds(10)).Result, In("PlaybackMoment").Name);

            // 點 Layout 上的「輸送站2」：警報 E205 沒有復歸
            var station = Retry.WhileNull(() => In("PlaybackLayout").FindFirstDescendant(cf.ByName("輸送站2")), TimeSpan.FromSeconds(10)).Result!;
            station.Click();
            Assert.True(Retry.WhileFalse(() => In("ElementStateTitle").Name == "輸送站2", TimeSpan.FromSeconds(10)).Result);
            var texts = string.Concat(In("ElementState").FindAllDescendants().Select(e => e.Name));
            Assert.Contains("未見復歸紀錄", texts);
            Assert.Contains("警報發生 A-02 E205", texts);

        }
        finally
        {
            app.Close();
            if (!app.HasExited) app.Kill();
        }
    }
}
