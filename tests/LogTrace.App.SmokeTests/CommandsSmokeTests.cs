using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using LogTrace.Tests.Support;

namespace LogTrace.App.SmokeTests;

// 命令、設備狀態表、未對應（#26）。測試資料：tests/fixtures/playback-prototype（虛構）
[Trait("Category", "UI")]
public sealed class CommandsSmokeTests : IDisposable
{
    readonly TempFolder _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void 選命令_點串起來的事件跳到該時刻_設備狀態表只看異常()
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
            AutomationElement[] Items(string id) =>
                In(id).FindAllChildren(cf.ByControlType(ControlType.DataItem).Or(cf.ByControlType(ControlType.ListItem)));

            In("PlantChoice").AsComboBox().Select("範例廠");
            In("NewCaseName").AsTextBox().Text = "命令案件";
            In("CreateCase").AsButton().Invoke();
            In("ImportSource").AsTextBox().Text = Path.Combine(TempFolder.Fixture("playback-prototype"), "logs");
            In("Import").AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => In("Status").Name.Contains("筆事件"), TimeSpan.FromSeconds(30)).Result);

            ImportSmokeTests.OpenTab(main, "命令");
            Assert.True(Retry.WhileFalse(() => Items("CommandList").Length == 3, TimeSpan.FromSeconds(10)).Result);
            Items("CommandList")[0].AsListBoxItem().Select();
            // 清單是虛擬化的，面板矮時只會產生看得到的列（事件數 6 筆由 ViewModel 測試確認）
            Assert.True(Retry.WhileFalse(() => Items("CommandEvents").Length > 1, TimeSpan.FromSeconds(10)).Result);
            Items("CommandEvents")[1].AsListBoxItem().Select(); // 派送 00040001 第1段（10:00:10）
            Assert.True(Retry.WhileFalse(() => In("CurrentTime").Name.Contains("10:00:10.0000"), TimeSpan.FromSeconds(10)).Result, In("CurrentTime").Name);

            ImportSmokeTests.OpenTab(main, "設備狀態表");
            Assert.True(Retry.WhileFalse(() => Items("EquipmentTable").Length > 0, TimeSpan.FromSeconds(10)).Result);
            In("OnlyAbnormal").AsCheckBox().IsChecked = true;
            Assert.True(Retry.WhileFalse(() => Items("EquipmentTable").Length == 0, TimeSpan.FromSeconds(10)).Result); // 10 秒時沒有異常
        }
        finally
        {
            app.Close();
            if (!app.HasExited) app.Kill();
        }
    }
}
