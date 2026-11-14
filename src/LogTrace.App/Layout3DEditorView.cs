using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LogTrace.Workspace.Layout;
using LogTrace.Workspace.Playback;

namespace LogTrace.App;

/// <summary>
/// Layout 編輯器的 3D 畫布（ADR-0018）：網頁把滑鼠動作換算成目前樓層平面上的座標（公尺）回報，這裡呼叫 ViewModel 既有的
/// 選取、拖曳、框選、貼上等方法（吸附、復原、存檔前檢查都在 ViewModel）。Layout 或選取改變時，把改變的元素與狀態傳給網頁。
/// </summary>
public sealed class Layout3DEditorView : Web3DView
{
    LayoutEditorViewModel? _vm;
    bool _pending;
    string? _floorsJson;
    Dictionary<string, string> _sent = [];
    string? _stateJson;
    bool _sceneSent;

    // 目前的滑鼠操作
    enum Mode { None, Move, Corner, Height, RailHandle, Transfer, Box }
    Mode _mode;
    double _startX, _startY;
    int _sx, _sy;

    protected override string Page => "editor.html";
    protected override string What => "編輯器的 3D 畫布";
    protected override string AutomationPrefix => "Editor3D";
    protected override string OtherFeatures => "元素清單、屬性面板、樓層列等照常使用，可以直接修改座標與尺寸。";

    bool _perspective;

    public Layout3DEditorView()
    {
        ToolBar.Children.Add(ToolButton("回到預設視角", "Editor3DResetView", () => Post("resetView", null)));
        ToolBar.Children.Add(ToolButton("俯視／斜視", "Editor3DProjection", () =>
        {
            _perspective = !_perspective;
            Post("projection", _perspective ? "perspective" : "orthographic");
        }, "切換正上方俯視（正交，預設）與斜上方 45 度透視"));
        ToolBar.Children.Add(new TextBlock
        {
            Text = "左鍵：點選／拖曳／空白處框選　右鍵：旋轉　中鍵：平移　滾輪：縮放",
            Foreground = System.Windows.Media.Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
        });
        DataContextChanged += (_, _) => Attach(DataContext as LayoutEditorViewModel);
    }

    void Attach(LayoutEditorViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnChanged;
            _vm.LayoutChanged -= Schedule;
            _vm.DocumentLoaded -= OnDocumentLoaded;
            _vm.Elements.CollectionChanged -= OnElementsChanged;
            foreach (var e in _vm.Elements) Unwatch(e);
        }
        _vm = vm;
        if (vm is null) return;
        vm.PropertyChanged += OnChanged;
        vm.LayoutChanged += Schedule;
        vm.DocumentLoaded += OnDocumentLoaded;
        vm.Elements.CollectionChanged += OnElementsChanged;
        foreach (var e in vm.Elements) Watch(e);
        Schedule();
    }

    void Watch(LayoutElementViewModel e)
    {
        e.PropertyChanged += OnChanged;
        e.RailHandles.CollectionChanged += OnCollectionChanged;
    }

    void Unwatch(LayoutElementViewModel e)
    {
        e.PropertyChanged -= OnChanged;
        e.RailHandles.CollectionChanged -= OnCollectionChanged;
    }

    void OnElementsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var x in e.OldItems?.OfType<LayoutElementViewModel>() ?? []) Unwatch(x);
        foreach (var x in e.NewItems?.OfType<LayoutElementViewModel>() ?? []) Watch(x);
        if (e.Action == NotifyCollectionChangedAction.Reset && _vm is not null)
            foreach (var x in _vm.Elements) { Unwatch(x); Watch(x); }
        Schedule();
    }

    void OnChanged(object? sender, PropertyChangedEventArgs e) => Schedule();

    /// <summary>開啟或新增設定：整個場景重畫，回到預設視角。</summary>
    void OnDocumentLoaded()
    {
        (_sceneSent, _keepView) = (false, false);
        Schedule();
    }

    bool _keepView;

    void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Schedule();

    /// <summary>同一輪的多個變更合併成一次傳送（拖曳時每次滑鼠移動只傳改變的元素）。</summary>
    void Schedule()
    {
        if (_pending) return;
        _pending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, Flush);
    }

    void Flush()
    {
        _pending = false;
        if (_vm is null || !IsReady) return;
        var scene = _vm.BuildScene();
        var floorsJson = PlaybackScene.ToJson(scene.Floors);
        var now = scene.Elements.ToDictionary(e => e.Id, e => PlaybackScene.ToJson(e), StringComparer.Ordinal);
        if (!_sceneSent || floorsJson != _floorsJson)
        {
            // 第一次或樓層改變：整個場景重畫；之後保留目前的視角
            Post("scene", new { layout = scene, keepView = _keepView });
            (_sceneSent, _keepView) = (true, true);
        }
        else
        {
            var changed = scene.Elements.Where(e => !_sent.TryGetValue(e.Id, out var old) || old != now[e.Id]).ToList();
            var removed = _sent.Keys.Where(id => !now.ContainsKey(id)).ToList();
            if (changed.Count > 0 || removed.Count > 0) Post("patch", new { elements = changed, removed });
        }
        (_floorsJson, _sent) = (floorsJson, now);
        var state = _vm.BuildSceneState();
        var stateJson = PlaybackScene.ToJson(state);
        if (stateJson != _stateJson)
        {
            Post("editorState", state);
            _stateJson = stateJson;
        }
    }

    protected override void OnReady()
    {
        (_sceneSent, _floorsJson, _stateJson, _sent) = (false, null, null, []);
        Schedule();
    }

    static double Num(JsonElement m, string name) => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
    static string? Str(JsonElement m, string name) => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static bool Flag(JsonElement m, string name) => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    protected override void OnPageMessage(string kind, JsonElement m)
    {
        if (_vm is null) return;
        switch (kind)
        {
            case "down":
                Down(m);
                break;
            case "move":
                Move(Num(m, "x"), Num(m, "y"), Num(m, "h"));
                break;
            case "up":
                Up(Num(m, "x"), Num(m, "y"), Flag(m, "ctrl"), Flag(m, "moved"));
                break;
            case "key":
                Key(Str(m, "key"), Flag(m, "ctrl"), m.TryGetProperty("x", out var x) && x.ValueKind == JsonValueKind.Number ? x.GetDouble() : null, Num(m, "y"));
                break;
        }
    }

    LayoutElementViewModel? Find(string? id) => id is null ? null : _vm!.Elements.FirstOrDefault(e => e.Id == id);

    void Down(JsonElement m)
    {
        var vm = _vm!;
        (_startX, _startY, _mode) = (Num(m, "x"), Num(m, "y"), Mode.None);
        var floor = Str(m, "floor");
        var target = m.TryGetProperty("target", out var t) ? t : default;
        var kind = target.ValueKind == JsonValueKind.Object ? Str(target, "kind") : null;
        var element = target.ValueKind == JsonValueKind.Object ? Find(Str(target, "id")) : null;
        // 在目前樓層的平面上編輯：點了別層時先切到那一層
        if (floor is not null && vm.Floors.Contains(floor) && floor != vm.CurrentFloor) vm.CurrentFloor = floor;
        switch (kind)
        {
            case "element" when element is not null:
                if (Flag(m, "ctrl"))
                {
                    vm.ToggleSelect(element);
                    return;
                }
                if (Num(m, "detail") >= 2 && element.IsRail)
                {
                    vm.InsertRailPointAt(element, _startX, _startY); // 雙擊導軌：插入一點
                    return;
                }
                vm.BeginDrag(element);
                _mode = Mode.Move;
                break;
            case "corner" when element is not null:
                vm.BeginDrag(element);
                (_sx, _sy, _mode) = ((int)Num(target, "sx"), (int)Num(target, "sy"), Mode.Corner);
                break;
            case "height" when element is not null:
                vm.BeginHeightDrag(element);
                _mode = Mode.Height;
                break;
            case "rotate" when element is not null:
                vm.SelectedElement = element;
                if (vm.RotateClockwiseCommand.CanExecute(null)) vm.RotateClockwiseCommand.Execute(null);
                break;
            case "railPoint" or "railSegment" when element is not null:
                var isPoint = kind == "railPoint";
                var index = (int)Num(target, "index");
                if (element.RailHandles.FirstOrDefault(h => h.IsPoint == isPoint && h.Index == index) is { } handle)
                {
                    vm.BeginRailHandleDrag(handle);
                    _mode = Mode.RailHandle;
                }
                break;
            case "from" or "to" when element is not null:
                vm.BeginTransferPointDrag(element, kind == "from");
                _mode = Mode.Transfer;
                break;
            default:
                _mode = Mode.Box; // 空白處：框選（放開時才選取）
                break;
        }
    }

    void Move(double x, double y, double h)
    {
        var vm = _vm!;
        switch (_mode)
        {
            case Mode.Move:
                vm.DragMoveBy(x - _startX, y - _startY);
                break;
            case Mode.Corner:
                vm.DragResizeCornerBy(_sx, _sy, x - _startX, y - _startY);
                break;
            case Mode.Height:
                vm.DragHeightTo(h);
                break;
            case Mode.RailHandle:
                vm.DragRailHandleTo(x, y);
                break;
            case Mode.Transfer:
                vm.DragTransferPointTo(x, y);
                break;
        }
    }

    void Up(double x, double y, bool ctrl, bool moved)
    {
        var vm = _vm!;
        var mode = _mode;
        _mode = Mode.None;
        if (mode == Mode.Box)
        {
            if (!moved)
            {
                if (!ctrl) vm.SelectedElement = null; // 點空白處：取消選取
                return;
            }
            vm.SelectInRect(_startX, _startY, x, y, ctrl);
            return;
        }
        if (mode != Mode.None) vm.EndDrag();
    }

    /// <summary>3D 畫布有焦點時的按鍵（網頁轉過來，#117 使用者決定與 2D 相同）。Ctrl+V 貼在滑鼠所在的目前樓層位置。</summary>
    void Key(string? key, bool ctrl, double? x, double y)
    {
        var vm = _vm!;
        switch (key?.ToLowerInvariant())
        {
            case "delete" when !ctrl:
                if (vm.DeleteSelectedCommand.CanExecute(null)) vm.DeleteSelectedCommand.Execute(null);
                break;
            case "c" when ctrl:
                if (vm.CopyCommand.CanExecute(null)) vm.CopyCommand.Execute(null);
                break;
            case "v" when ctrl:
                vm.Paste(x, x is null ? null : y);
                break;
            case "z" when ctrl:
                if (vm.UndoCommand.CanExecute(null)) vm.UndoCommand.Execute(null);
                break;
        }
    }
}
