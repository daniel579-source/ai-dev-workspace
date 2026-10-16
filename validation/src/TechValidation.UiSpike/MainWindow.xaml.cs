using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AvalonDock.Core;
using AvalonDock.Layout;
using AvalonDock.Serializer.Xml;
using TechValidation.Core;

namespace TechValidation.UiSpike;

public sealed record EventRow(long Seq, string Time, string Module, string Logger, string Flags, string Message);

public partial class MainWindow : Window
{
    const int EventCount = 100_000;
    readonly Dictionary<string, object> _contents = new();
    string _defaultLayout = "";
    readonly Stopwatch _fpsClock = Stopwatch.StartNew();
    int _fpsFrames;

    static string LayoutFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TechValidation", "layout.xml");

    public MainWindow()
    {
        InitializeComponent();
        var (rows, ticks, alarms) = FakeEvents(EventCount);
        EventGrid.ItemsSource = rows;
        EventListView.ItemsSource = rows;
        Timeline.SetData(ticks, alarms);
        Loaded += OnLoaded;
        CompositionTarget.Rendering += (_, _) =>
        {
            _fpsFrames++;
            if (_fpsClock.ElapsedMilliseconds >= 1000)
            {
                FpsText.Text = $"{_fpsFrames * 1000.0 / _fpsClock.ElapsedMilliseconds:F0} fps";
                _fpsFrames = 0;
                _fpsClock.Restart();
            }
        };
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        foreach (var a in Dock.Layout.Descendents().OfType<LayoutAnchorable>()) _contents[a.ContentId] = a.Content;
        _defaultLayout = SerializeLayout();
        AppendLine(EnvironmentReport());
        if (App.SelfTestOutput is not null)
        {
            await Task.Delay(1500); // 等第一次版面配置完成
            await RunSelfTest(App.SelfTestOutput);
            if (App.ExitAfterSelfTest) Close();
        }
    }

    // ---------------- 虛構資料 ----------------

    static (List<EventRow>, long[], long[]) FakeEvents(int n)
    {
        var rnd = new Random(7);
        var modules = new[] { ("EXE", "WebServiceLOG"), ("EXE", "EXEDebug"), ("PLC", "PLCLOG"), ("PLC", "BCR_LOG"), ("Server", "WCSDebug"), ("Server", "DBUPDATE_LOG") };
        var start = new DateTime(2030, 1, 7).Ticks;
        var ticks = new long[n];
        long t = start;
        for (int i = 0; i < n; i++) { t += rnd.Next(0, 3) == 0 ? 0 : rnd.Next(1, 17_000_000); ticks[i] = t; }
        var rows = new List<EventRow>(n);
        var alarms = new List<long>();
        for (int i = 0; i < n; i++)
        {
            var (m, l) = modules[rnd.Next(modules.Length)];
            var stn = $"A-{rnd.Next(1, 49):00}";
            string msg = rnd.Next(500) == 0 ? $"[告警] {stn} 設備門開啟異常（虛構）" : $"[{stn}][Station_Status_Change] => 在席 = {(rnd.Next(2) == 0 ? "ON" : "OFF")}";
            if (msg.StartsWith("[告警]")) alarms.Add(ticks[i]);
            rows.Add(new EventRow(i + 1, new DateTime(ticks[i]).ToString("yyyy-MM-dd HH:mm:ss.ffff"), m, l,
                i > 0 && ticks[i] == ticks[i - 1] ? "＝" : "", msg));
        }
        return (rows, ticks, alarms.ToArray());
    }

    // ---------------- 版面（ADR-0007 第 2 項） ----------------

    string SerializeLayout()
    {
        var ser = new XmlLayoutSerializer(Dock);
        using var w = new StringWriter();
        ser.Serialize(w);
        return w.ToString();
    }

    void DeserializeLayout(string xml)
    {
        // AvalonDock 5：序列化改為獨立套件（Dirkster.AvalonDock.Serializer.Xml），以 ContentId 對回既有內容
        var ser = new XmlLayoutSerializer(Dock) { UnresolvedContentHandling = UnresolvedContentHandling.Remove };
        ser.LayoutSerializationCallback += (_, args) =>
        {
            if (args.Model.ContentId is { } id && _contents.TryGetValue(id, out var c)) args.Content = c;
        };
        using var r = new StringReader(xml);
        ser.Deserialize(r);
    }

    void SaveLayout_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LayoutFile)!);
        File.WriteAllText(LayoutFile, SerializeLayout());
        AppendLine($"版面已儲存：{LayoutFile}");
    }

    void LoadLayout_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(LayoutFile)) { AppendLine("尚未儲存版面"); return; }
        try { DeserializeLayout(File.ReadAllText(LayoutFile)); AppendLine("版面已載入"); }
        catch (Exception ex) { AppendLine("版面檔無法載入，改用預設：" + ex.Message); DeserializeLayout(_defaultLayout); }
    }

    void ResetLayout_Click(object sender, RoutedEventArgs e)
    {
        DeserializeLayout(_defaultLayout);
        AppendLine("已還原預設版面");
    }

    // ---------------- 自我檢查 ----------------

    async void SelfTest_Click(object sender, RoutedEventArgs e) => await RunSelfTest(null);

    async Task RunSelfTest(string? outputPath)
    {
        var result = new Dictionary<string, object?>();
        AppendLine("=== 自我檢查開始 ===");
        result["environment"] = EnvironmentFacts();

        // 1. 10 萬筆事件清單捲動（DataGrid 與 ListView 比較）
        foreach (var (contentId, list, label) in new (string, ItemsControl, string)[] { ("events", EventGrid, "DataGrid"), ("eventsListView", EventListView, "ListView") })
        {
            Dock.Layout.Descendents().OfType<LayoutAnchorable>().First(a => a.ContentId == contentId).IsSelected = true;
            await Task.Delay(500);
            var sv = FindChild<ScrollViewer>(list)!;
            sv.ScrollToTop();
            await NextFrame();
            // ScrollUnit=Item 時位移以「列」計：每格約 14 列（約 300 px）
            double step = sv.ExtentHeight > EventCount * 2 ? 300 : 14;
            var smooth = await RunFrames(300, i => sv.ScrollToVerticalOffset(sv.VerticalOffset + step));
            Report(result, $"事件清單（{label}）：連續捲動（每格約 300 px）", smooth);
            var rnd = new Random(1);
            var jump = await RunFrames(200, i => sv.ScrollToVerticalOffset(rnd.NextDouble() * sv.ScrollableHeight));
            Report(result, $"事件清單（{label}）：拖曳捲軸（每格跳到隨機位置）", jump);
        }
        Dock.Layout.Descendents().OfType<LayoutAnchorable>().First(a => a.ContentId == "events").IsSelected = true;

        // 1. 時間軸縮放與平移
        var tl = await RunFrames(300, i => Timeline.Animate(i));
        Report(result, "時間軸：縮放＋平移", tl);

        // 4. 2D 畫布拖曳與縮放
        var drag = await RunFrames(300, i => CanvasHost.SimulateDrag(i));
        Report(result, "Layout 畫布：拖曳元素", drag);
        var zoom = await RunFrames(300, i => CanvasHost.SimulateZoomPan(i));
        Report(result, "Layout 畫布：縮放＋平移", zoom);

        // 2. AvalonDock 版面儲存與還原
        result["layout_roundtrip"] = await LayoutRoundTrip();

        if (outputPath is not null)
        {
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            File.WriteAllText(outputPath, json, new UTF8Encoding(false));
            AppendLine($"結果已寫入：{Path.GetFullPath(outputPath)}");
        }
        AppendLine("=== 自我檢查結束 ===");
    }

    async Task<Dictionary<string, object>> LayoutRoundTrip()
    {
        var before = SerializeLayout();
        // 改變版面：把時間軸變成浮動視窗、把自我檢查面板切到前景
        var tlAnch = Dock.Layout.Descendents().OfType<LayoutAnchorable>().First(a => a.ContentId == "timeline");
        tlAnch.Float();
        await NextFrame();
        var changed = SerializeLayout();
        DeserializeLayout(before);
        await NextFrame();
        var after = SerializeLayout();
        // 損壞的版面檔：應能退回預設
        bool corruptHandled;
        try { DeserializeLayout("<LayoutRoot><broken"); corruptHandled = false; }
        catch { DeserializeLayout(_defaultLayout); corruptHandled = true; }
        await NextFrame();
        var r = new Dictionary<string, object>
        {
            ["changed_differs"] = changed != before,
            ["restored_equals_original"] = after == before,
            ["corrupt_file_falls_back"] = corruptHandled,
            ["all_contents_present_after_restore"] = Dock.Layout.Descendents().OfType<LayoutAnchorable>().Count(a => a.Content is not null) == _contents.Count,
        };
        AppendLine("版面：" + string.Join("，", r.Select(kv => $"{kv.Key}={kv.Value}")));
        return r;
    }

    void Report(Dictionary<string, object?> result, string name, FrameStats s)
    {
        result[name] = s;
        AppendLine($"{name}：平均 {s.AvgMs:F1} ms（{s.Fps:F0} fps），p95 {s.P95Ms:F1} ms，最大 {s.MaxMs:F1} ms，超過 33 ms 的格數 {s.Over33}/{s.Frames}，UI 執行緒工作 平均 {s.WorkAvgMs:F1}／p95 {s.WorkP95Ms:F1} ms → {(s.P95Ms <= 33.4 ? "順暢" : "可能卡頓")}");
    }

    /// <summary>AvgMs/P95Ms：格與格的間隔；WorkAvgMs/WorkP95Ms：UI 執行緒上的動作＋版面配置＋OnRender 時間。</summary>
    public sealed record FrameStats(int Frames, double AvgMs, double P95Ms, double MaxMs, int Over33, double Fps, double WorkAvgMs, double WorkP95Ms);

    static Task NextFrame()
    {
        var tcs = new TaskCompletionSource();
        EventHandler? h = null;
        h = (_, _) => { CompositionTarget.Rendering -= h; tcs.SetResult(); };
        CompositionTarget.Rendering += h;
        return tcs.Task;
    }

    /// <summary>每一格執行一次動作，記錄格與格之間的時間（包含版面配置與繪製的成本）。</summary>
    async Task<FrameStats> RunFrames(int frames, Action<int> perFrame)
    {
        var tcs = new TaskCompletionSource();
        var times = new List<double>(frames);
        var work = new List<double>(frames);
        var sw = Stopwatch.StartNew();
        double last = -1;
        int i = 0;
        EventHandler? h = null;
        h = (_, _) =>
        {
            var now = sw.Elapsed.TotalMilliseconds;
            if (last >= 0) times.Add(now - last);
            last = now;
            if (i >= frames) { CompositionTarget.Rendering -= h; tcs.SetResult(); return; }
            var w0 = sw.Elapsed.TotalMilliseconds;
            perFrame(i++);
            UpdateLayout();
            work.Add(sw.Elapsed.TotalMilliseconds - w0);
        };
        CompositionTarget.Rendering += h;
        await tcs.Task;
        times.Sort();
        work.Sort();
        return new FrameStats(times.Count, times.Average(), times[(int)(times.Count * 0.95)], times[^1], times.Count(t => t > 33.4), 1000 / times.Average(),
            work.Average(), work[(int)(work.Count * 0.95)]);
    }

    static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) return t;
            if (FindChild<T>(c) is { } r) return r;
        }
        return null;
    }

    // ---------------- 環境（T-01、T-13、ADR-0007 第 3 項） ----------------

    static Dictionary<string, object> EnvironmentFacts()
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location) ?? "";
        var selfContained = string.Equals(Path.GetFullPath(runtimeDir).TrimEnd('\\'), Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        var trigram = CaseStore.TrigramAvailable(out var sqliteDetail);
        var nfkc = TextNormalizer.SelfCheck(out var nfkcDetail);
        var cases = NormalizationCases.All.Count(c => TextNormalizer.Normalize(c.Input) == c.Expected);
        return new()
        {
            ["os"] = RuntimeInformation.OSDescription,
            ["framework"] = RuntimeInformation.FrameworkDescription,
            ["self_contained"] = selfContained,
            ["runtime_dir"] = runtimeDir,
            ["sqlite"] = sqliteDetail,
            ["fts5_trigram_ok"] = trigram,
            ["nfkc_ok"] = nfkc,
            ["nfkc_detail"] = nfkcDetail,
            ["normalization_cases_passed"] = $"{cases}/{NormalizationCases.All.Length}",
            ["icu"] = IcuMode(),
            ["wpf_render_tier"] = RenderCapability.Tier >> 16,
        };
    }

    static string IcuMode()
    {
        // .NET 5+ 在 Windows 10 1903 以後使用系統的 ICU；更早的版本退回 NLS
        try
        {
            var t = Type.GetType("System.Globalization.GlobalizationMode, System.Private.CoreLib");
            var p = t?.GetProperty("UseNls", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            return p?.GetValue(null) is true ? "NLS" : "ICU";
        }
        catch { return "未知"; }
    }

    static string EnvironmentReport() =>
        string.Join(Environment.NewLine, EnvironmentFacts().Select(kv => $"{kv.Key}: {kv.Value}"));

    void AppendLine(string s)
    {
        SelfTestText.AppendText(s + Environment.NewLine);
        SelfTestText.ScrollToEnd();
    }
}
