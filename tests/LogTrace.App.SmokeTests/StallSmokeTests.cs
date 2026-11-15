using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using LogTrace.Tests.Support;

namespace LogTrace.App.SmokeTests;

// 可能卡住（推估，ADR-0005 第 5 點 2026-11-16 補充，#142）。測試資料：tests/fixtures/stall（虛構；案件 09:00～09:09，
// 命令 00050001 第 1 段 09:00:40 抵達輸送站3 後，09:07:40 才派送第 2 段 → 09:05:40～09:07:40 可能卡住）
[Trait("Category", "UI")]
public sealed class StallSmokeTests : IDisposable
{
    readonly TempFolder _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void 點時間軸總覽到卡住的時段_點選卡住的站點_詳細資訊寫出可能卡住的依據()
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
            In("NewCaseName").AsTextBox().Text = "卡住案件";
            In("CreateCase").AsButton().Invoke();
            In("ImportSource").AsTextBox().Text = TempFolder.Fixture("stall");
            In("Import").AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => In("Status").Name.Contains("筆事件"), TimeSpan.FromSeconds(30)).Result, In("Status").Name);
            Assert.True(Retry.WhileFalse(() => In("Playback3DStatus").Name.StartsWith("3D 已載入", StringComparison.Ordinal), TimeSpan.FromSeconds(30)).Result,
                In("Playback3DStatus").Name);

            // 總覽的 6.5／9 ＝ 09:06:30，落在可能卡住的時段
            var overview = In("OverviewTimeline").BoundingRectangle;
            Mouse.Click(new System.Drawing.Point((int)(overview.Left + overview.Width * 6.5 / 9), (int)(overview.Bottom - 5)));
            Assert.True(Retry.WhileFalse(() => In("CurrentTime").Name.Contains("09:06"), TimeSpan.FromSeconds(10)).Result, In("CurrentTime").Name);

            // 3D：點卡住的站點（名稱標籤；3D 的「可能卡住」標記由 Workspace 測試 SceneNow.Stalled 涵蓋，
            // WebView2 的 UIA 名稱在文字改變後不一定更新，所以這裡不比對標籤文字）
            var label = Retry.WhileNull(() => In("Playback3D").FindAllDescendants().FirstOrDefault(e => e.Name.StartsWith("輸送站3", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(20)).Result ?? throw new InvalidOperationException("3D 畫面中找不到「輸送站3」");
            label.Click();
            Assert.True(Retry.WhileFalse(() => In("ElementStateTitle").Name == "輸送站3", TimeSpan.FromSeconds(10)).Result);
            var texts = string.Concat(In("ElementState").FindAllDescendants().Select(e => e.Name));
            Assert.Contains("推估：命令 00050001 第 1 段抵達後 7 分鐘沒有下一步", texts);
        }
        finally
        {
            app.Close();
            if (!app.HasExited) app.Kill();
        }
    }
}
