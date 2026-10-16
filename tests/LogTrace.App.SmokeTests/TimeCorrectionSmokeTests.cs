using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using LogTrace.Tests.Support;

namespace LogTrace.App.SmokeTests;

// 時區與時間偏移量（#19）。測試資料：tests/fixtures/two-modules（虛構）
[Trait("Category", "UI")]
public sealed class TimeCorrectionSmokeTests : IDisposable
{
    readonly TempFolder _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void 頂端顯示時區_設定模組偏移量後標示已校正()
    {
        var dataRoot = Path.Combine(_tmp.Path, "AppData");
        using var app = FlaUI.Core.Application.Launch(new ProcessStartInfo(ImportSmokeTests.AppExe(), $"--data-root \"{dataRoot}\""));
        using var automation = new UIA3Automation();
        try
        {
            var main = app.GetMainWindow(automation, TimeSpan.FromSeconds(30))!;
            var cf = main.ConditionFactory;
            AutomationElement In(AutomationElement root, string id) =>
                Retry.WhileNull(() => root.FindFirstDescendant(cf.ByAutomationId(id)), TimeSpan.FromSeconds(10)).Result
                ?? throw new InvalidOperationException($"找不到 {id}");

            In(main, "NewCaseName").AsTextBox().Text = "時間校正";
            In(main, "CreateCase").AsButton().Invoke();
            In(main, "ImportSource").AsTextBox().Text = TempFolder.Fixture("two-modules");
            In(main, "Import").AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => In(main, "Status").Name.Contains("筆事件"), TimeSpan.FromSeconds(30)).Result);
            Assert.Equal("時區：Asia/Taipei（UTC+08:00）", In(main, "TimeZone").Name);

            In(main, "EditTimeCorrection").AsButton().Invoke();
            var dialog = Retry.WhileNull(() => main.ModalWindows.FirstOrDefault(), TimeSpan.FromSeconds(10)).Result!;
            In(dialog, "ModuleOffset_PLC").AsTextBox().Text = "abc";
            In(dialog, "ApplyTimeCorrection").AsButton().Invoke();
            Assert.Contains("PLC", In(dialog, "TimeCorrectionError").Name); // 輸入有誤：不關閉
            In(dialog, "ModuleOffset_PLC").AsTextBox().Text = "4.5";
            In(dialog, "ApplyTimeCorrection").AsButton().Invoke();

            Assert.True(Retry.WhileFalse(() => In(main, "Status").Name.Contains("已設定時間偏移量"), TimeSpan.FromSeconds(30)).Result, In(main, "Status").Name);
            Assert.Equal("已校正（PLC +4.5 秒）", In(main, "CorrectionText").Name);
        }
        finally
        {
            app.Close();
            if (!app.HasExited) app.Kill();
        }
    }
}
