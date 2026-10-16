using System.IO;
using System.Windows;
using System.Windows.Controls;
using AvalonDock;
using AvalonDock.Layout;
using AvalonDock.Core;
using AvalonDock.Serializer.Xml;

namespace LogTrace.App;

/// <summary>
/// Docking 版面（ADR-0008 第 7 點）：兩種預設版面（回放、搜尋）、依使用者保存、還原預設。
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
    }

    /// <summary>所有面板的 ContentId。</summary>
    public IReadOnlyCollection<string> PanelIds => _panels.Keys;

    /// <summary>套用預設版面。面板的內容先從目前的版面取下，才能放進新的版面。</summary>
    public void ApplyPreset(string name)
    {
        foreach (var a in _dock.Layout.Descendents().OfType<LayoutAnchorable>().ToList()) a.Content = null;
        _dock.Layout = Build(name);
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
            var ids = _dock.Layout.Descendents().OfType<LayoutAnchorable>().Select(a => a.ContentId).ToHashSet();
            if (ids.SetEquals(_panels.Keys)) return true;
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
            pane.Children.Add(new LayoutAnchorable { ContentId = id, Title = _panels[id].Title, Content = _panels[id].Content, CanClose = false, CanHide = false });
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
    /// 回放：Layout 為主，事件清單在旁；下方為詳細資訊、元素狀態等分頁、原始 LOG。
    /// 搜尋：事件清單為主，搜尋面板在前；下方為原始 LOG、Layout、元素狀態等分頁。
    /// </summary>
    LayoutRoot Build(string name)
    {
        var states = new[] { "elementstate", "commands", "equipment", "unmapped" };
        LayoutPanel main = name switch
        {
            Search => Panel(1, H, H,
                Pane(1, H, "search", "cases"),
                Panel(4, H, V,
                    Panel(5, V, H, Pane(1.6, H, "events"), Pane(1, H, "detail")),
                    Pane(1.4, V, "timeline"),
                    Panel(3, V, H, Pane(1.4, H, "rawlog"), Pane(1, H, "playback"), Pane(1, H, states)))),
            _ => Panel(1, H, H,
                Pane(1, H, "cases", "search"),
                Panel(4, H, V,
                    Panel(5, V, H, Pane(1, H, "events"), Pane(1.2, H, "playback")),
                    Pane(1.4, V, "timeline"),
                    Panel(3, V, H, Pane(1, H, "detail"), Pane(1, H, states), Pane(1, H, "rawlog")))),
        };
        return new LayoutRoot { RootPanel = main };
    }
}
