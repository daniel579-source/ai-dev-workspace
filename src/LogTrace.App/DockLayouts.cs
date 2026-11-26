using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AvalonDock;
using AvalonDock.Layout;
using AvalonDock.Controls;
using AvalonDock.Core;
using AvalonDock.Serializer.Xml;
using LogTrace.Workspace;

namespace LogTrace.App;

/// <summary>
/// Docking 版面（ADR-0008 第 7 點）：兩種預設版面（回放、搜尋）、依使用者保存、還原預設。
/// 面板可以關閉（隱藏），以「視窗」選單開啟或關閉（ADR-0029）。
/// 面板的內容只在 MainWindow.xaml 宣告一次；排列方式由這裡建立，再以 ContentId 對回內容（ADR-0007）。
/// </summary>
public sealed class DockLayouts
{
    public const string Playback = "回放";
    public const string Search = "搜尋";

    readonly DockingManager _dock;
    readonly Dictionary<string, (string Title, object Content)> _panels;

    public DockLayouts(DockingManager dock)
    {
        _dock = dock;
        _panels = dock.Layout.Descendents().OfType<LayoutAnchorable>().ToDictionary(a => a.ContentId, a => (a.Title, a.Content));
        // AvalonDock 自己的處理（只改變分隔線兩側的面板）在分隔線上先執行；這裡在之後依比例調整同一排的其他面板
        dock.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnSplitterDragStarted), handledEventsToo: true);
        dock.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnSplitterDragCompleted), handledEventsToo: true);
    }

    /// <summary>開始拖動時同一排面板的大小：面板、拖動前的比例、像素大小、最小大小。</summary>
    sealed record Drag(LayoutGridResizerControl Splitter, Orientation Orientation, List<(Sizing Model, double Star, double Size, double Min)> Items, int Split);

    Drag? _drag;

    /// <summary>面板或面板群組的寬高比例（AvalonDock 的共同介面不公開，依實際類別存取）。</summary>
    sealed record Sizing(Func<bool, GridLength> Get, Action<bool, GridLength> Set, Func<bool, double> Min)
    {
        public static Sizing? Of(object? model) => model switch
        {
            LayoutPanel g => From(g),
            LayoutAnchorablePane g => From(g),
            LayoutAnchorablePaneGroup g => From(g),
            LayoutDocumentPane g => From(g),
            LayoutDocumentPaneGroup g => From(g),
            _ => null,
        };

        static Sizing From<T>(LayoutPositionableGroup<T> g) where T : class, ILayoutElement => new(
            h => h ? g.DockWidth : g.DockHeight,
            (h, v) => { if (h) g.DockWidth = v; else g.DockHeight = v; },
            h => h ? g.DockMinWidth : g.DockMinHeight);
    }

    void OnSplitterDragStarted(object sender, DragStartedEventArgs e)
    {
        _drag = null;
        if (e.OriginalSource is not LayoutGridResizerControl splitter || VisualTreeHelper.GetParent(splitter) is not Grid grid
            || grid is not ILayoutControl { Model: LayoutPanel panel }) return;
        var horizontal = panel.Orientation == Orientation.Horizontal;
        var items = new List<(Sizing, double, double, double)>();
        int split = -1;
        foreach (UIElement child in grid.Children)
        {
            if (child == splitter) split = items.Count;
            if (child is LayoutGridResizerControl || child.Visibility != Visibility.Visible || child is not FrameworkElement fe) continue;
            var m = Sizing.Of((child as ILayoutControl)?.Model);
            var length = m?.Get(horizontal) ?? GridLength.Auto;
            if (m is null || !length.IsStar) return; // 認不得的面板或固定大小的面板：只用 AvalonDock 原本的方式
            items.Add((m, length.Value, horizontal ? fe.ActualWidth : fe.ActualHeight, m.Min(horizontal)));
        }
        if (split >= 1 && split < items.Count) _drag = new Drag(splitter, panel.Orientation, items, split);
    }

    /// <summary>
    /// 拖動分隔線後，同一排的面板依比例一起縮放（分隔線前面的全部放大或縮小，後面的也一樣）。
    /// 分隔線移動的距離由 AvalonDock 改過的前一個面板比例推算。
    /// </summary>
    void OnSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        var drag = _drag;
        _drag = null;
        if (drag is null || e.OriginalSource != drag.Splitter || e.Canceled) return;
        bool horizontal = drag.Orientation == Orientation.Horizontal;
        var prev = drag.Items[drag.Split - 1];
        var now = prev.Model.Get(horizontal);
        if (!now.IsStar || prev.Star <= 0 || prev.Size <= 0) return;
        double delta = prev.Size * (now.Value / prev.Star - 1);
        if (Math.Abs(delta) < 0.5) return;

        var sizes = PaneSizes.Resize(drag.Items.Select(i => i.Size).ToList(), drag.Split, delta, drag.Items.Select(i => i.Min).ToList());
        double total = drag.Items.Sum(i => i.Size), stars = drag.Items.Sum(i => i.Star);
        if (total <= 0) return;
        for (int i = 0; i < sizes.Length; i++)
        {
            drag.Items[i].Model.Set(horizontal, Star(sizes[i] * stars / total)); // 維持原本比例數值的大小範圍
        }
    }

    /// <summary>所有面板的 ContentId。</summary>
    public IReadOnlyCollection<string> PanelIds => _panels.Keys;

    /// <summary>「視窗」選單的順序（ADR-0029）。</summary>
    public static IReadOnlyList<string> MenuOrder { get; } =
        ["cases", "search", "events", "playback", "timeline", "detail", "elementstate", "commands", "equipment", "unmapped", "knowledge", "rawlog", "incidents"];

    /// <summary>面板的標題。</summary>
    public string TitleOf(string id) => _panels.TryGetValue(id, out var p) ? p.Title : id;

    LayoutAnchorable? Find(string id) => _dock.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(a => a.ContentId == id);

    /// <summary>面板是否顯示中（沒有被關閉）。</summary>
    public bool IsShown(string id) => Find(id) is { IsHidden: false };

    /// <summary>開啟或關閉面板（關閉是隱藏，開啟時回到關閉前的位置，ADR-0029）。</summary>
    public void SetShown(string id, bool shown)
    {
        if (Find(id) is not { } a) return;
        if (shown && a.IsHidden)
        {
            a.Show();
            a.IsActive = true; // 開啟的面板在同一區的分頁中顯示出來
        }
        else if (!shown && !a.IsHidden) a.Hide();
    }

    /// <summary>
    /// 套用預設版面。面板的內容先從目前的版面取下，才能放進新的版面。keepClosed 為 true 時（切換回放／搜尋），
    /// 使用者關掉的面板維持關閉；「還原預設版面」傳 false，所有面板都顯示（ADR-0029 第 5 點）。
    /// </summary>
    public void ApplyPreset(string name, bool keepClosed = false)
    {
        var closed = keepClosed ? MenuOrder.Where(id => Find(id) is { IsHidden: true }).ToList() : [];
        foreach (var a in _dock.Layout.Descendents().OfType<LayoutAnchorable>().ToList()) a.Content = null;
        _dock.Layout = Build(name);
        foreach (var id in closed) Find(id)?.Hide(); // 隱藏後從「視窗」選單開啟時放回這個版面中的位置
    }

    /// <summary>目前的版面（XML）。</summary>
    public string Save()
    {
        using var w = new StringWriter();
        new XmlLayoutSerializer(_dock).Serialize(w);
        return w.ToString();
    }

    /// <summary>
    /// 還原保存的版面。版面檔損壞、或少了某個面板（來自舊版程式）時改用「回放」預設版面，回傳 false。
    /// </summary>
    public bool TryRestore(string xml)
    {
        try
        {
            Load(xml);
            var all = _dock.Layout.Descendents().OfType<LayoutAnchorable>().ToList(); // 含關閉（隱藏）的面板
            var ids = all.Select(a => a.ContentId).ToHashSet();
            if (ids.SetEquals(_panels.Keys))
            {
                // 舊版程式存的版面標示面板不能隱藏：一律改為可以關閉（ADR-0029）
                foreach (var a in all) (a.CanHide, a.CanClose) = (true, false);
                return true;
            }
        }
        catch (Exception) // 版面檔的內容不可預期（XmlSerializer 包裝的各種例外）；任何失敗都退回預設，不影響啟動
        {
        }
        ApplyPreset(Playback);
        return false;
    }

    void Load(string xml)
    {
        var ser = new XmlLayoutSerializer(_dock) { UnresolvedContentHandling = UnresolvedContentHandling.Remove };
        ser.LayoutSerializationCallback += (_, args) =>
        {
            if (args.Model.ContentId is { } id && _panels.TryGetValue(id, out var p)) args.Content = p.Content;
            else args.Cancel = true; // 認不得的面板（來自其他版本的程式）不顯示
        };
        using var r = new StringReader(xml);
        ser.Deserialize(r);
    }

    static GridLength Star(double v) => new(v, GridUnitType.Star);

    LayoutAnchorablePane Pane(double size, Orientation along, params string[] ids)
    {
        var pane = new LayoutAnchorablePane();
        foreach (var id in ids)
            pane.Children.Add(new LayoutAnchorable { ContentId = id, Title = _panels[id].Title, Content = _panels[id].Content, CanClose = false, CanHide = true }); // 關閉＝隱藏（ADR-0029）
        pane.SelectedContentIndex = 0; // 同一區的第一個分頁預設顯示
        SetSize(pane, size, along);
        return pane;
    }

    static LayoutPanel Panel(double size, Orientation along, Orientation orientation, params ILayoutPanelElement[] children)
    {
        var panel = new LayoutPanel { Orientation = orientation };
        foreach (var c in children) panel.Children.Add(c);
        SetSize(panel, size, along);
        return panel;
    }

    /// <summary>以比例設定寬或高（固定值會被 AvalonDock 忽略）。</summary>
    static void SetSize(LayoutAnchorablePane e, double size, Orientation along)
    {
        if (along == Orientation.Horizontal) e.DockWidth = Star(size);
        else e.DockHeight = Star(size);
    }

    static void SetSize(LayoutPanel e, double size, Orientation along)
    {
        if (along == Orientation.Horizontal) e.DockWidth = Star(size);
        else e.DockHeight = Star(size);
    }

    const Orientation H = Orientation.Horizontal, V = Orientation.Vertical;

    /// <summary>
    /// 回放：Layout 為主（知識庫是同一區的分頁），事件清單在旁；下方為詳細資訊、元素狀態等分頁、原始 LOG、異常紀錄。
    /// 搜尋：事件清單為主，搜尋面板在前，詳細資訊在旁（知識庫是同一區的分頁）；下方為原始 LOG、Layout、元素狀態等分頁、異常紀錄。
    /// 知識庫放在主要區域，查看時有足夠的空間；同一區的第一個分頁（Layout、詳細資訊）預設顯示。
    /// </summary>
    LayoutRoot Build(string name)
    {
        var states = new[] { "elementstate", "commands", "equipment", "unmapped" };
        LayoutPanel main = name switch
        {
            Search => Panel(1, H, H,
                Pane(1, H, "search", "cases"),
                Panel(4, H, V,
                    Panel(5, V, H, Pane(1.6, H, "events"), Pane(1, H, "detail", "knowledge")),
                    Pane(1.4, V, "timeline"),
                    Panel(3, V, H, Pane(1.4, H, "rawlog"), Pane(1, H, "playback"), Pane(1, H, states), Pane(1.4, H, "incidents")))),
            _ => Panel(1, H, H,
                Pane(1, H, "cases", "search"),
                Panel(4, H, V,
                    Panel(5, V, H, Pane(1, H, "events"), Pane(1.2, H, "playback", "knowledge")),
                    Pane(1.4, V, "timeline"),
                    Panel(3, V, H, Pane(1, H, "detail"), Pane(1, H, states), Pane(1, H, "rawlog"), Pane(1.4, H, "incidents")))),
        };
        return new LayoutRoot { RootPanel = main };
    }
}
