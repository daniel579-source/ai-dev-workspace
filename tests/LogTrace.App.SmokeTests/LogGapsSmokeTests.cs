using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using LogTrace.Tests.Support;

namespace LogTrace.App.SmokeTests;

// 沒有 LOG 的時段（#84，AC-011）。測試資料：tests/fixtures/log-gaps（虛構；PLC 刻意刪除 08:10～08:20 的 LOG，案件為 08:00～08:30）
[Trait("Category", "UI")]
public sealed class LogGapsSmokeTests : IDisposable
{
    readonly TempFolder _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void 點時間軸總覽到刻意刪除的時段_回放與詳細資訊提示沒有LOG()
    {
        var dataRoot = Path.Combine(_tmp.Path, "AppData");
        var plants = Directory.CreateDirectory(Path.Combine(dataRoot, "Plants")).FullName;
        File.Copy(Path.Combine(TempFolder.Fixture("playback-prototype"), "plant.json"), Path.Combine(plants, "範例廠.json"));

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
            In("NewCaseName").AsTextBox().Text = "缺口案件";
            In("CreateCase").AsButton().Invoke();
            In("ImportSource").AsTextBox().Text = TempFolder.Fixture("log-gaps");
            In("Import").AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => In("Status").Name.Contains("筆事件"), TimeSpan.FromSeconds(30)).Result);

            // 總覽的正中央＝08:15，落在 PLC 沒有 LOG 的時段
            var overview = In("OverviewTimeline").BoundingRectangle;
            Mouse.Click(new System.Drawing.Point((int)(overview.Left + overview.Width / 2), (int)(overview.Bottom - 5)));
            Assert.True(Retry.WhileFalse(() => In("CurrentTime").Name.Contains("08:1"), TimeSpan.FromSeconds(10)).Result, In("CurrentTime").Name);

            Assert.True(Retry.WhileFalse(() => In("PlaybackGapNotice").Name.Contains("模組 PLC 沒有 LOG（08:10:00～08:20:00，10 分鐘"),
                TimeSpan.FromSeconds(10)).Result);
            Assert.Contains("模組 PLC 沒有 LOG", ImportSmokeTests.DocumentText(In("EventDetail")));
        }
        finally
        {
            app.Close();
            if (!app.HasExited) app.Kill();
        }
    }
}
