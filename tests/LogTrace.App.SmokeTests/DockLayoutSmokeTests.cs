using System.Diagnostics;
using System.Xml.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using LogTrace.Tests.Support;

namespace LogTrace.App.SmokeTests;

// Docking 版面：兩種預設版面、依使用者保存、還原預設、版面檔損壞（#28，ADR-0008 第 7 點）
[Trait("Category", "UI")]
public sealed class DockLayoutSmokeTests : IDisposable
{
    static readonly string[] Panels = ["案件", "搜尋", "事件清單", "Layout 回放", "時間軸", "詳細資訊", "元素狀態", "命令", "設備狀態表", "未對應", "知識庫", "原始 LOG", "異常紀錄"];

    readonly TempFolder _tmp = new();
    readonly string _dataRoot;

    public DockLayoutSmokeTests() => _dataRoot = Path.Combine(_tmp.Path, "AppData");

    public void Dispose() => _tmp.Dispose();

    string LayoutFile => Path.Combine(_dataRoot, "layout-2.xml");

    /// <summary>版面的結構（面板的排列與順序），不含大小。</summary>
    string Structure()
    {
        var ids = XDocument.Load(LayoutFile).Descendants("LayoutAnchorable").Select(e => (string?)e.Attribute("ContentId")).ToList();
        Assert.Equal(Panels.Length, ids.Distinct().Count());
        return
        string.Join(" ", XDocument.Load(LayoutFile).Root!.Element("RootPanel")!.DescendantsAndSelf()
            .Where(e => e.Name.LocalName is "LayoutPanel" or "LayoutAnchorablePane" or "LayoutAnchorable")
            .Select(e => e.Name.LocalName switch
            {
                "LayoutPanel" => "P:" + (string?)e.Attribute("Orientation"),
                "LayoutAnchorablePane" => "|",
                _ => (string?)e.Attribute("ContentId"),
            }));
    }

    /// <summary>啟動程式、做一些操作、關閉（關閉時保存版面）。</summary>
    void Run(Action<Window, Func<string, AutomationElement>> act)
    {
        using var app = FlaUI.Core.Application.Launch(new ProcessStartInfo(ImportSmokeTests.AppExe(), $"--data-root \"{_dataRoot}\""));
        using var automation = new UIA3Automation();
        try
        {
            var main = app.GetMainWindow(automation, TimeSpan.FromSeconds(30))!;
            var cf = main.ConditionFactory;
            // 選單展開後的項目可能在桌面底下的另一個視窗
            AutomationElement In(string id) =>
                Retry.WhileNull(() => main.FindFirstDescendant(cf.ByAutomationId(id)) ?? automation.GetDesktop().FindFirstDescendant(cf.ByAutomationId(id)),
                    TimeSpan.FromSeconds(10)).Result ?? throw new InvalidOperationException($"找不到 {id}");
            Assert.True(Retry.WhileFalse(() => AllPanelsShown(main), TimeSpan.FromSeconds(10)).Result,
                "有面板沒有顯示：" + string.Join("、", Panels.Where(t => main.FindFirstDescendant(cf.ByName(t)) is null)));
            act(main, In);
        }
        finally
        {
            app.Close();
            Assert.True(Retry.WhileFalse(() => app.HasExited, TimeSpan.FromSeconds(10)).Result);
        }
    }

    /// <summary>每個面板的標題都在畫面上（分頁的標題、或單一面板的標題列）。</summary>
    static bool AllPanelsShown(Window main) => Panels.All(t => main.FindFirstDescendant(main.ConditionFactory.ByName(t)) is not null);

    static void Choose(Func<string, AutomationElement> @in, string item)
    {
        @in("LayoutMenu").AsMenuItem().Expand();
        @in(item).AsMenuItem().Invoke();
        // Invoke 可能在程式處理點選之前就返回：等程式處理完，再讓呼叫端關閉程式（否則可能存下舊的版面）
        FlaUI.Core.Input.Wait.UntilInputIsProcessed(TimeSpan.FromMilliseconds(500));
        Thread.Sleep(300);
    }

    [Fact]
    public void 兩種預設版面_關閉後重新開啟相同_還原預設與初次啟動相同()
    {
        Run((_, _) => { });
        var initial = Structure(); // 初次啟動：回放
        Assert.Contains("| playback knowledge |", initial); // 知識庫在主要區域，是 Layout 回放同一區的分頁

        Run((main, @in) =>
        {
            Choose(@in, "LayoutSearch");
            Assert.True(Retry.WhileFalse(() => AllPanelsShown(main), TimeSpan.FromSeconds(10)).Result);
        });
        var search = Structure();
        Assert.NotEqual(initial, search);
        Assert.StartsWith("| search cases P:Vertical", search); // 搜尋面板在前
        Assert.Contains("| detail knowledge |", search); // 知識庫是詳細資訊同一區的分頁

        Run((_, _) => { }); // 重新開啟：維持搜尋版面
        Assert.Equal(search, Structure());

        Run((_, @in) => Choose(@in, "LayoutReset"));
        Assert.Equal(initial, Structure());
    }

    [Fact]
    public void 版面檔損壞時仍能啟動並使用預設版面()
    {
        Run((_, _) => { });
        var initial = Structure();

        File.WriteAllText(LayoutFile, "<LayoutRoot><RootPanel>這不是完整的版面"); // 損壞
        Run((_, @in) => Assert.Contains("預設版面", @in("Status").Name));
        Assert.Equal(initial, Structure());

        // 來自舊版程式：少了一個面板
        var old = XDocument.Load(LayoutFile);
        old.Descendants("LayoutAnchorable").First(e => (string?)e.Attribute("ContentId") == "unmapped").Remove();
        old.Save(LayoutFile);
        Run((_, @in) => Assert.Contains("預設版面", @in("Status").Name));
        Assert.Equal(initial, Structure());
    }

    [Fact]
    public void 舊版程式的版面檔_新版第一次開啟改用新的預設版面一次_並刪除舊檔()
    {
        Run((_, _) => { });
        var initial = Structure();

        var old = Path.Combine(_dataRoot, "layout.xml");
        File.Move(LayoutFile, old); // 舊版程式只有 layout.xml
        Run((_, @in) => Assert.Contains("版面已更新", @in("Status").Name));
        Assert.False(File.Exists(old));
        Assert.Equal(initial, Structure());

        Run((_, @in) => Assert.DoesNotContain("版面已更新", @in("Status").Name)); // 只有一次
    }

    /// <summary>版面檔中各面板所在區域的寬度比例（DockWidth，例如「1.4*」）。</summary>
    double Width(string contentId)
    {
        var pane = XDocument.Load(LayoutFile).Descendants("LayoutAnchorable").First(e => (string?)e.Attribute("ContentId") == contentId).Parent!;
        var w = ((string?)pane.Attribute("DockWidth") ?? "1*").TrimEnd('*');
        return w.Length == 0 ? 1 : double.Parse(w, System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void 拖動分隔線_同一排的其他面板依比例一起縮放()
    {
        string dragged = "";
        Run((main, _) =>
        {
            // 下方一排：詳細資訊｜元素狀態等分頁｜原始 LOG｜異常紀錄。把詳細資訊右邊的分隔線往右拖 120 像素
            // 測試用真實的滑鼠：先把視窗移到最上層，避免其他視窗蓋住分隔線（#201）
            main.SetForeground();
            FlaUI.Core.Input.Wait.UntilInputIsProcessed(TimeSpan.FromMilliseconds(300));
            var title = main.FindFirstDescendant(main.ConditionFactory.ByName("詳細資訊"))!.BoundingRectangle;
            System.Drawing.Rectangle[] Splitters() => main.FindAllDescendants(main.ConditionFactory.ByControlType(FlaUI.Core.Definitions.ControlType.Thumb))
                .Select(t => t.BoundingRectangle)
                .Where(r => r.Height > r.Width && r.Left > title.Left && r.Top <= title.Bottom && r.Bottom >= title.Top)
                .OrderBy(r => r.Left).ToArray();
            var splitter = Splitters().First();
            var from = splitter.Center();
            var to = new System.Drawing.Point(from.X + 120, from.Y);
            FlaUI.Core.Input.Mouse.Drag(from, to);
            // 等畫面上的分隔線真的移到拖曳終點附近，才關閉程式（關閉時保存版面）；不用固定的等待時間
            var moved = Retry.WhileFalse(() => Splitters().Any(r => Math.Abs(r.Center().X - to.X) <= 10), TimeSpan.FromSeconds(5)).Result;
            dragged = $"分隔線 {splitter} 拖到 {to}；拖曳後的分隔線 {string.Join("、", Splitters())}";
            Assert.True(moved, "分隔線沒有移動（滑鼠可能被其他操作干擾）：" + dragged);
        });

        double states = Width("elementstate"), raw = Width("rawlog"), incidents = Width("incidents"), detail = Width("detail");
        var widths = $"詳細資訊 {detail}、元素狀態 {states}、原始 LOG {raw}、異常紀錄 {incidents}；{dragged}";
        Assert.True(detail > states, widths);
        // 分隔線右邊的面板依同一比例縮小：彼此的比例不變（預設 1：1：1.4）
        Assert.True(Math.Abs(states / raw - 1.0) < 0.005, widths);
        Assert.True(Math.Abs(incidents / raw - 1.4) < 0.005, widths);
    }
}
