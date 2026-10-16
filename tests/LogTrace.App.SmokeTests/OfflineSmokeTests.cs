using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using LogTrace.Tests.Support;

namespace LogTrace.App.SmokeTests;

// 執行期間沒有任何對外連線（#29，ADR-0001：完全離線）。測試資料：tests/fixtures/playback-prototype（虛構）
[Trait("Category", "UI")]
public sealed class OfflineSmokeTests : IDisposable
{
    readonly TempFolder _tmp = new();

    public void Dispose() => _tmp.Dispose();

    /// <summary>以 netstat 列出這個程序擁有的 TCP／UDP 端點（含監聽與已建立的連線）。</summary>
    static List<string> Endpoints(int pid)
    {
        var psi = new ProcessStartInfo("netstat", "-ano") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var lines = p.StandardOutput.ReadToEnd().Split('\n');
        p.WaitForExit();
        return lines.Select(l => l.Trim())
            .Where(l => l.StartsWith("TCP", StringComparison.Ordinal) || l.StartsWith("UDP", StringComparison.Ordinal))
            .Where(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() == pid.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
    }

    [Fact]
    public void 走完主要流程_程式沒有開啟任何網路連線()
    {
        var dataRoot = Path.Combine(_tmp.Path, "AppData");
        Directory.CreateDirectory(Path.Combine(dataRoot, "Plants"));
        File.Copy(Path.Combine(TempFolder.Fixture("playback-prototype"), "plant.json"), Path.Combine(dataRoot, "Plants", "範例廠.json"));

        using var app = FlaUI.Core.Application.Launch(new ProcessStartInfo(ImportSmokeTests.AppExe(), $"--data-root \"{dataRoot}\""));
        using var automation = new UIA3Automation();
        var seen = new List<string>();
        try
        {
            var main = app.GetMainWindow(automation, TimeSpan.FromSeconds(30))!;
            var cf = main.ConditionFactory;
            AutomationElement In(string id) =>
                Retry.WhileNull(() => main.FindFirstDescendant(cf.ByAutomationId(id)), TimeSpan.FromSeconds(10)).Result
                ?? throw new InvalidOperationException($"找不到 {id}");
            void Check() => seen.AddRange(Endpoints(app.ProcessId));

            Check(); // 啟動後
            In("PlantChoice").AsComboBox().Select("範例廠");
            In("NewCaseName").AsTextBox().Text = "離線";
            In("CreateCase").AsButton().Invoke();
            In("ImportSource").AsTextBox().Text = Path.Combine(TempFolder.Fixture("playback-prototype"), "logs");
            In("Import").AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => In("Status").Name.Contains("筆事件"), TimeSpan.FromSeconds(30)).Result);
            Check(); // 匯入、建立索引

            ImportSmokeTests.OpenTab(main, "搜尋");
            In("SearchAllOf").AsTextBox().Text = "SSCA01";
            In("Search").AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => In("SearchStatus").Name.StartsWith("符合"), TimeSpan.FromSeconds(10)).Result);
            In("EventList").Focus();
            Keyboard.Type(VirtualKeyShort.END);
            Keyboard.Type(VirtualKeyShort.SPACE); // 播放一下
            Thread.Sleep(1000);
            Keyboard.Type(VirtualKeyShort.SPACE);
            Check(); // 搜尋、回放

            In("OpenLayoutEditor").AsButton().Invoke();
            Thread.Sleep(1500);
            Check(); // Layout 編輯器

            Assert.True(seen.Count == 0, "程式開啟了網路端點：\n" + string.Join("\n", seen.Distinct()));
        }
        finally
        {
            app.Close();
            if (!app.HasExited) app.Kill();
        }
    }
}
