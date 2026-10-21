using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogTrace.Engine.Cases;
using LogTrace.Engine.Plants;

namespace LogTrace.Workspace.Layout;

/// <summary>
/// Layout 編輯器（ADR-0006 第 9 點）：三欄式——左邊元素清單與工具箱、中間畫布、右邊屬性。
/// 編輯廠別設定檔（JSON）；畫布上的拖曳、調整大小以公尺計，吸附到 0.5 公尺的格線。
/// 「載入案件比對」列出案件中出現、但還沒對應的編號，拖到元素上即成為別名（ADR-0006 第 3 點）。
/// </summary>
public sealed partial class LayoutEditorViewModel : ObservableObject
{
    /// <summary>格線吸附的間距（公尺）。</summary>
    public const double Snap = 0.5;

    public static readonly IReadOnlyList<int> Rotations = [0, 90, 180, 270];

    readonly PlantConfigLibrary _library;
    readonly IUserInteraction _ui;
    readonly CaseLibrary? _cases;
    IReadOnlyList<(string Kind, string Value, long Count)> _caseIds = [];
    PlantConfigDocument _document = new();
    List<(LayoutElementViewModel Element, double X, double Y, double W, double H)>? _drag;
    LayoutElementViewModel? _dragPrimary;
    (LayoutElementViewModel Conveyor, bool From)? _pointDrag;

    /// <param name="cases">「載入案件比對」可以選的案件；null 時不提供。</param>
    public LayoutEditorViewModel(PlantConfigLibrary library, IUserInteraction ui, CaseLibrary? cases = null)
    {
        _library = library;
        _ui = ui;
        _cases = cases;
        Elements.CollectionChanged += (_, _) =>
        {
            RefreshElementList();
            RefreshEndpoints();
            RefreshUnmapped();
            Commit(null);
        };
        RefreshConfigs();
        RefreshCaseChoices();
        Load(new PlantConfigDocument { Plant = "新廠別" }, null);
        Status = "建立新的廠別設定，或從清單開啟既有的設定。";
    }

    public IReadOnlyList<LayoutElementType> AddableTypes => LayoutElementTypes.Addable;

    /// <summary>存放位置下的廠別設定檔。</summary>
    public ObservableCollection<string> Configs { get; } = [];

    /// <summary>元素；順序即畫布的上下層與存檔順序。</summary>
    public ObservableCollection<LayoutElementViewModel> Elements { get; } = [];

    /// <summary>元素清單：依 <see cref="LayoutElementTypes.ListOrder"/> 排序，不影響 <see cref="Elements"/> 的順序。</summary>
    public ObservableCollection<LayoutElementViewModel> ElementList { get; } = [];

    /// <summary>輸送線兩端可以選的元素（站點、對接設備、搬送機），依 <see cref="LayoutElementTypes.ListOrder"/> 排序。</summary>
    public ObservableCollection<LayoutElementViewModel> ConveyorEndpoints { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial string? FilePath { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial string PlantName { get; set; } = "";

    [ObservableProperty]
    public partial string StorageCodeRule { get; set; } = "";

    // ---- LOG 設定（ADR-0002 第 7 點：建立案件時選擇廠別，匯入時使用） ----

    /// <summary>同時間戳記時的模組順序，一行一個（ADR-0004 第 5 點）。</summary>
    [ObservableProperty]
    public partial string ModuleOrderText { get; set; } = "";

    /// <summary>已知的 LOG 種類（Logger 名稱），一行一個；不在清單中的標示「未知種類」（ADR-0004）。</summary>
    [ObservableProperty]
    public partial string KnownLoggersText { get; set; } = "";

    /// <summary>依模組與 Logger 指定編碼；沒有指定的檔案自動判斷。</summary>
    public ObservableCollection<EncodingRuleViewModel> EncodingRules { get; } = [];

    public IReadOnlyList<string> EncodingChoices => EncodingRuleViewModel.Choices;

    partial void OnModuleOrderTextChanged(string value) => IsDirty = true;
    partial void OnKnownLoggersTextChanged(string value) => IsDirty = true;

    static List<string> Lines(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    void AddRule(EncodingRuleViewModel rule)
    {
        rule.PropertyChanged += (_, _) => IsDirty = true;
        EncodingRules.Add(rule);
    }

    [RelayCommand]
    void AddEncodingRule()
    {
        AddRule(new EncodingRuleViewModel("", "", Engine.Parsing.LogEncoding.Big5Name));
        IsDirty = true;
    }

    [RelayCommand]
    void RemoveEncodingRule(EncodingRuleViewModel? rule)
    {
        if (rule is not null && EncodingRules.Remove(rule)) IsDirty = true;
    }

    [ObservableProperty]
    public partial int Revision { get; private set; }

    [ObservableProperty]
    public partial string? RevisionInfo { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial bool IsDirty { get; private set; }

    [ObservableProperty]
    public partial string Status { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand), nameof(AddAliasCommand), nameof(AddPropertyCommand), nameof(SetRotationCommand))]
    public partial LayoutElementViewModel? SelectedElement { get; set; }

    public bool HasSelection => SelectedElement is not null;

    /// <summary>畫布大小（公尺）：元素範圍再加 20 公尺，最小 60 × 40（ADR-0006 第 11 點）。不寫進設定檔。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanvasSizeText))]
    public partial double CanvasWidth { get; private set; } = LayoutGeometry.MinCanvasWidth;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanvasSizeText))]
    public partial double CanvasHeight { get; private set; } = LayoutGeometry.MinCanvasHeight;

    public string CanvasSizeText => $"{CanvasWidth:0} × {CanvasHeight:0} 公尺";

    /// <summary>畫面縮放（1 = 100%）；只改變顯示。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomText))]
    public partial double Zoom { get; set; } = 1;

    public string ZoomText => LayoutZoom.Text(Zoom);

    partial void OnZoomChanged(double value)
    {
        var clamped = LayoutZoom.Clamp(value);
        if (clamped != value) Zoom = clamped;
    }

    /// <summary>Ctrl＋滾輪。</summary>
    public void ZoomStep(bool zoomIn) => Zoom = LayoutZoom.Step(Zoom, zoomIn);

    /// <summary>「符合視窗」：可視範圍與每公尺像素數由畫面提供。</summary>
    public void FitZoom(double viewportWidth, double viewportHeight, double pixelsPerMeter) =>
        Zoom = LayoutZoom.Fit(viewportWidth, viewportHeight, CanvasWidth * pixelsPerMeter, CanvasHeight * pixelsPerMeter);

    /// <summary>屬性面板中輸入、準備加入的別名。</summary>
    [ObservableProperty]
    public partial string NewAlias { get; set; } = "";

    public string Title => $"Layout 編輯器 - {(FilePath is null ? "（尚未存檔）" : Path.GetFileName(FilePath))}{(IsDirty ? " *" : "")}";

    partial void OnSelectedElementChanged(LayoutElementViewModel? oldValue, LayoutElementViewModel? newValue)
    {
        if (_selecting) return;
        foreach (var e in Elements) e.IsSelected = e == newValue;
        OnSelectionChanged();
    }

    partial void OnPlantNameChanged(string value) => IsDirty = true;
    partial void OnStorageCodeRuleChanged(string value) => IsDirty = true;

    // ---- 檔案 ----

    void RefreshConfigs()
    {
        Configs.Clear();
        foreach (var p in _library.List()) Configs.Add(p);
    }

    void Load(PlantConfigDocument doc, string? path)
    {
        _suppressUndo++;
        try
        {
            foreach (var e in Elements) Detach(e);
            _document = doc;
            SelectedElement = null;
            Elements.Clear();
            foreach (var e in doc.Layout.Elements) Attach(new LayoutElementViewModel(e));
        }
        finally
        {
            _suppressUndo--;
        }
        ResetUndo();
        PlantName = doc.Plant;
        StorageCodeRule = doc.Layout.StorageCodeRule ?? "";
        ModuleOrderText = string.Join(Environment.NewLine, doc.ModuleOrder);
        KnownLoggersText = string.Join(Environment.NewLine, doc.KnownLoggers);
        EncodingRules.Clear();
        foreach (var r in doc.Encodings) AddRule(EncodingRuleViewModel.From(r));
        ParseRules.Clear();
        foreach (var r in doc.ParseRules) AddParseRule(ParseRuleViewModel.From(r));
        PresenceSignal = doc.PresenceSignal;
        StationModeNames.Clear();
        foreach (var (code, name) in doc.StationModeNames) AddModeName(new StationModeNameViewModel(code, name));
        GapThresholds.Clear();
        foreach (var (module, seconds) in doc.GapThresholds) AddGapThreshold(new GapThresholdViewModel(module, seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Revision = doc.Revision;
        RevisionInfo = doc.Revision == 0 ? "尚未存檔"
            : doc.RevisedAt is { } at ? $"修訂 {doc.Revision}（{doc.RevisedBy}，{at:yyyy-MM-dd HH:mm}）"
            : $"修訂 {doc.Revision}";
        FilePath = path;
        RefreshEndpoints();
        IsDirty = false;
    }

    /// <summary>有未存檔的修改時詢問是否放棄；可以繼續時傳回 true。</summary>
    public bool ConfirmDiscard() =>
        !IsDirty || _ui.Confirm("尚未存檔", $"「{PlantName}」有尚未存檔的修改，確定要放棄嗎？");

    [RelayCommand]
    void New()
    {
        if (!ConfirmDiscard()) return;
        Load(new PlantConfigDocument { Plant = "新廠別" }, null);
        Status = "已建立新的廠別設定（尚未存檔）。";
    }

    /// <summary>開啟存放位置下的設定檔。</summary>
    [RelayCommand]
    void OpenConfig(string? path)
    {
        if (path is null || !ConfirmDiscard()) return;
        Open(path);
    }

    /// <summary>開啟其他位置的設定檔（例如同事分享的檔案）。</summary>
    [RelayCommand]
    void OpenFile()
    {
        if (!ConfirmDiscard()) return;
        if (_ui.PickPlantConfigFile(_library.Root) is { } path) Open(path);
    }

    void Open(string path)
    {
        try
        {
            Load(PlantConfigFile.Load(path), path);
            Status = $"已開啟 {Path.GetFileName(path)}。";
        }
        catch (Exception ex) when (ex is PlantConfigFormatException or IOException or UnauthorizedAccessException)
        {
            Status = "無法開啟：" + ex.Message;
        }
    }

    [RelayCommand]
    void Save() => SaveTo(FilePath ?? _library.SuggestPath(PlantName));

    [RelayCommand]
    void SaveAs()
    {
        var suggested = FilePath is null ? _library.SuggestPath(PlantName) : Path.GetFileName(FilePath);
        if (_ui.PickPlantConfigSavePath(_library.Root, Path.GetFileName(suggested)) is { } path) SaveTo(path);
    }

    void SaveTo(string path)
    {
        foreach (var e in Elements) e.HasIssue = false;
        _document.Plant = PlantName.Trim();
        _document.Layout.StorageCodeRule = string.IsNullOrWhiteSpace(StorageCodeRule) ? null : StorageCodeRule.Trim();
        _document.Layout.Elements = Elements.Select(e => e.ToModel()).ToList();
        _document.ModuleOrder = Lines(ModuleOrderText);
        _document.KnownLoggers = Lines(KnownLoggersText);
        _document.Encodings = EncodingRules.Select(r => r.ToModel()).ToList();
        _document.ParseRules = ParseRules.Select(r => r.ToModel()).ToList();
        _document.PresenceSignal = PresenceSignal.Trim();
        _document.StationModeNames = StationModeNames.Select(r => r.ToModel()).ToList();
        _document.GapThresholds = GapThresholds.Select(r => r.ToModel()).ToList();
        try
        {
            PlantConfigFile.Save(_document, path);
        }
        catch (PlantConfigValidationException ex)
        {
            var ids = ex.Issues.SelectMany(i => i.ElementIds).ToHashSet();
            foreach (var e in Elements.Where(e => ids.Contains(e.Id))) e.HasIssue = true;
            SelectedElement = Elements.FirstOrDefault(e => e.HasIssue) ?? SelectedElement;
            Status = "無法存檔：" + string.Join("；", ex.Issues.Select(i => i.Message));
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "存檔失敗：" + ex.Message;
            return;
        }
        Load(_document, path);
        RefreshConfigs();
        Status = $"已存檔：{Path.GetFileName(path)}，修訂 {Revision}。";
    }

    // ---- 元素 ----

    void Attach(LayoutElementViewModel e)
    {
        e.PropertyChanged += OnElementChanged;
        e.Aliases.CollectionChanged += OnElementCollectionChanged;
        e.Props.CollectionChanged += OnElementCollectionChanged;
        foreach (var p in e.Props) WatchProperty(p);
        Elements.Add(e);
    }

    void Detach(LayoutElementViewModel e)
    {
        e.PropertyChanged -= OnElementChanged;
        e.Aliases.CollectionChanged -= OnElementCollectionChanged;
        e.Props.CollectionChanged -= OnElementCollectionChanged;
    }

    void OnElementCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        IsDirty = true;
        RefreshUnmapped();
        Commit(null);
    }

    /// <summary>自訂屬性的鍵或值改變：同一列連續輸入合併成一個復原步驟。</summary>
    void WatchProperty(PropertyItemViewModel item) =>
        item.PropertyChanged += (_, _) =>
        {
            IsDirty = true;
            Commit("prop:" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(item));
        };

    void OnElementChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LayoutElementViewModel.HasIssue) or nameof(LayoutElementViewModel.IsSelected)
            or nameof(LayoutElementViewModel.AliasesText) or nameof(LayoutElementViewModel.LineX1) or nameof(LayoutElementViewModel.LineY1)
            or nameof(LayoutElementViewModel.LineX2) or nameof(LayoutElementViewModel.LineY2)
            or nameof(LayoutElementViewModel.CenterX) or nameof(LayoutElementViewModel.CenterY)
            or nameof(LayoutElementViewModel.HasFromPoint) or nameof(LayoutElementViewModel.HasToPoint)
            or nameof(LayoutElementViewModel.TransferPointsText))
            return;
        IsDirty = true;
        _suppressUndo++; // 連帶的變更（交接點、線端）和這次修改算同一步
        try
        {
            if (sender is LayoutElementViewModel { IsConveyor: true } c && e.PropertyName is nameof(LayoutElementViewModel.From) or nameof(LayoutElementViewModel.To))
                ResetTransferPoint(c, e.PropertyName == nameof(LayoutElementViewModel.From));
            RefreshAfterChange(e.PropertyName);
        }
        finally
        {
            _suppressUndo--;
        }
        if (sender is LayoutElementViewModel changed) Commit($"{changed.Id}.{e.PropertyName}");
    }

    void RefreshAfterChange(string? propertyName)
    {
        if (propertyName is nameof(LayoutElementViewModel.X) or nameof(LayoutElementViewModel.Y) or nameof(LayoutElementViewModel.W)
            or nameof(LayoutElementViewModel.H) or nameof(LayoutElementViewModel.From) or nameof(LayoutElementViewModel.To)
            or nameof(LayoutElementViewModel.Name) or nameof(LayoutElementViewModel.FromPoint) or nameof(LayoutElementViewModel.ToPoint))
        {
            if (propertyName is nameof(LayoutElementViewModel.Name)) RefreshElementList();
            RefreshEndpoints();
        }
        else if (propertyName is nameof(LayoutElementViewModel.Rotation))
            RefreshCanvasSize();
    }

    /// <summary>依元素範圍重算畫布大小：拖到靠近邊緣時跟著變大。</summary>
    void RefreshCanvasSize()
    {
        (CanvasWidth, CanvasHeight) = LayoutGeometry.CanvasSize(Elements.Select(e => e.ToModel()));
    }

    LayoutElementViewModel? Find(string? id) => id is null ? null : Elements.FirstOrDefault(e => e.Id == id && e.HasGeometry);

    /// <summary>
    /// 輸送線的一端改了：連到搬送機時，交接點預設為搬送機圖示上最靠近另一端的邊緣點；不是搬送機時清除交接點（ADR-0006 第 7 點）。
    /// </summary>
    void ResetTransferPoint(LayoutElementViewModel conveyor, bool from)
    {
        var end = Find(from ? conveyor.From : conveyor.To);
        LayoutPoint? point = null;
        if (end?.Type == LayoutElementType.Vehicle)
        {
            var other = Find(from ? conveyor.To : conveyor.From);
            var otherPoint = from ? conveyor.ToPoint : conveyor.FromPoint;
            var (tx, ty) = other?.Type == LayoutElementType.Vehicle && otherPoint is { } op ? (op.X, op.Y)
                : other is not null ? (other.CenterX, other.CenterY) : (end.CenterX, end.CenterY);
            point = LayoutGeometry.NearestEdgePoint(end.X, end.Y, end.W, end.H, end.Rotation, tx, ty);
        }
        if (from) conveyor.FromPoint = point;
        else conveyor.ToPoint = point;
    }

    /// <summary>更新輸送線兩端的位置，以及可以當作輸送線兩端的元素。</summary>
    void RefreshEndpoints()
    {
        var byId = Elements.Where(e => e.HasGeometry).ToDictionary(e => e.Id);
        foreach (var c in Elements.Where(e => e.IsConveyor))
        {
            if (c.From is { } f && byId.TryGetValue(f, out var a))
                (c.LineX1, c.LineY1) = a.Type == LayoutElementType.Vehicle && c.FromPoint is { } p ? (p.X, p.Y) : (a.CenterX, a.CenterY);
            if (c.To is { } t && byId.TryGetValue(t, out var b))
                (c.LineX2, c.LineY2) = b.Type == LayoutElementType.Vehicle && c.ToPoint is { } q ? (q.X, q.Y) : (b.CenterX, b.CenterY);
        }
        RefreshCanvasSize();
        SyncOrder(ConveyorEndpoints, Elements.Where(e => LayoutElement.CanBeConveyorEnd(e.Type)).Order(LayoutElementTypes.ListOrder).ToList());
    }

    void RefreshElementList() => SyncOrder(ElementList, Elements.Order(LayoutElementTypes.ListOrder).ToList());

    /// <summary>就地調整成指定的順序（移動，不清空），清單與下拉選單的選取不會因此被清掉。</summary>
    static void SyncOrder<T>(ObservableCollection<T> target, IReadOnlyList<T> desired) where T : class
    {
        for (int i = target.Count - 1; i >= 0; i--)
            if (!desired.Contains(target[i])) target.RemoveAt(i);
        for (int i = 0; i < desired.Count; i++)
        {
            int k = target.IndexOf(desired[i]);
            if (k < 0) target.Insert(i, desired[i]);
            else if (k != i) target.Move(k, i);
        }
    }

    string NextId(LayoutElementType type)
    {
        var prefix = LayoutElementTypes.IdPrefix(type) + "-";
        int n = 1;
        while (Elements.Any(e => e.Id == prefix + n)) n++;
        return prefix + n;
    }

    /// <summary>新增元素，放在現有元素的下方，並選取它。</summary>
    [RelayCommand]
    void AddElement(LayoutElementType type)
    {
        var id = NextId(type);
        LayoutElement model;
        if (type == LayoutElementType.Conveyor)
        {
            var ends = Elements.Where(e => LayoutElement.CanBeConveyorEnd(e.Type)).ToList(); // 依加入的順序，不受清單排序影響
            if (ends.Count < 2)
            {
                Status = "輸送線需要至少兩個站點、對接設備或搬送機。";
                return;
            }
            model = new LayoutElement { Id = id, Type = type, Name = $"輸送線 {id[(id.IndexOf('-') + 1)..]}", From = ends[0].Id, To = ends[1].Id };
            var conveyor = new LayoutElementViewModel(model);
            ResetTransferPoint(conveyor, from: true);
            ResetTransferPoint(conveyor, from: false);
            Attach(conveyor);
            SelectedElement = conveyor;
            IsDirty = true;
            Status = $"已新增{conveyor.TypeLabel}。";
            return;
        }
        else
        {
            var (w, h, height) = LayoutElementTypes.DefaultSize(type);
            // 放在現有元素的下方，避免疊在一起
            var top = SnapTo(Elements.Where(e => e.HasGeometry).Select(e => e.Y + e.H).DefaultIfEmpty(0).Max() + 1);
            model = new LayoutElement
            {
                Id = id, Type = type, Name = $"新{LayoutElementTypes.Label(type)} {id[(id.IndexOf('-') + 1)..]}",
                X = 1, Y = top, W = w, H = h, Rotation = 0, Height = height,
                Aliases = LayoutElement.CanHaveAliases(type) ? [] : null,
            };
            if (type == LayoutElementType.Rack)
                (model.Row, model.ColFrom, model.ColTo, model.Levels, model.LevelHeight) = ("01", 1, 10, 5, 2.5);
        }
        var vm = new LayoutElementViewModel(model);
        Attach(vm);
        SelectedElement = vm;
        IsDirty = true;
        Status = $"已新增{vm.TypeLabel}。";
    }

    bool CanEditSelection() => SelectedElement is not null;

    bool HasAnySelection() => Elements.Any(e => e.IsSelected);

    /// <summary>刪除所有選取的元素（Del）；連到它們的輸送線一起刪除（ADR-0006 第 10 點）。</summary>
    [RelayCommand(CanExecute = nameof(HasAnySelection))]
    void DeleteSelected()
    {
        var selected = Selection;
        if (selected.Count == 0) return;
        var ids = selected.Where(e => e.HasGeometry).Select(e => e.Id).ToHashSet();
        var removed = Elements.Where(x => x.IsSelected || (x.IsConveyor && (ids.Contains(x.From ?? "") || ids.Contains(x.To ?? "")))).ToList();
        SetSelection([]);
        Batch(() =>
        {
            foreach (var x in removed)
            {
                Detach(x);
                Elements.Remove(x);
            }
        });
        IsDirty = true;
        var extra = removed.Count - selected.Count;
        var what = selected.Count == 1 ? $"「{selected[0].Name}」" : $" {selected.Count} 個元素";
        Status = extra > 0 ? $"已刪除{what}與連到的 {extra} 條輸送線。" : $"已刪除{what}。";
    }

    // ---- 選取（ADR-0006 第 10 點）：IsSelected 標示所有選取的元素；只選一個時 SelectedElement 為它，屬性面板才顯示 ----

    bool _selecting;

    /// <summary>目前選取的元素（依清單順序）。</summary>
    public IReadOnlyList<LayoutElementViewModel> Selection => Elements.Where(e => e.IsSelected).ToList();

    public int SelectionCount => Elements.Count(e => e.IsSelected);

    /// <summary>屬性面板沒有顯示單一元素時的說明。</summary>
    public string SelectionHint => SelectionCount > 1
        ? $"已選取 {SelectionCount} 個元素：拖曳一起移動，Del 刪除，Ctrl+C 複製。屬性面板只在選取一個元素時顯示。"
        : "點選畫布上的元素即可編輯屬性；拖曳移動，拖右下角的橘色方塊調整大小。在空白處拖曳可以框選，Ctrl＋點選加選或取消選取。";

    void SetSelection(IEnumerable<LayoutElementViewModel> items)
    {
        var set = items.ToHashSet();
        _selecting = true;
        try
        {
            foreach (var e in Elements) e.IsSelected = set.Contains(e);
            SelectedElement = set.Count == 1 ? set.First() : null;
        }
        finally
        {
            _selecting = false;
        }
        OnSelectionChanged();
    }

    void OnSelectionChanged()
    {
        _lastEditKey = null; // 換了選取後的修改另算一步
        OnPropertyChanged(nameof(SelectionCount));
        OnPropertyChanged(nameof(SelectionHint));
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Ctrl＋點選：加選或取消選取。</summary>
    public void ToggleSelect(LayoutElementViewModel element) =>
        SetSelection(element.IsSelected ? Selection.Where(e => e != element) : Selection.Append(element));

    /// <summary>
    /// 框選（公尺）：框碰到的元素就算選到，碰到的輸送線也算。add 為 true（按住 Ctrl）時加到原本的選取。
    /// </summary>
    public void SelectInRect(double x1, double y1, double x2, double y2, bool add)
    {
        var rect = (Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
        var hits = Elements.Where(e => e.HasGeometry
            ? LayoutGeometry.RectsTouch(LayoutGeometry.Bounds(e.X, e.Y, e.W, e.H, e.Rotation), rect)
            : LayoutGeometry.SegmentTouchesRect(e.LineX1, e.LineY1, e.LineX2, e.LineY2, rect)).ToList();
        SetSelection(add ? Selection.Concat(hits) : hits);
        if (SelectionCount > 1) Status = $"已選取 {SelectionCount} 個元素：拖曳一起移動，Del 刪除，Ctrl+C 複製。";
    }

    // ---- 剪貼簿（編輯器內部，不用 Windows 剪貼簿） ----

    string? _clipboard;

    /// <summary>
    /// 複製選取的元素（Ctrl+C）。連到選取元素的輸送線一起複製：兩端都被選取時貼上後改連到新元素，
    /// 只有一端被選取時另一端連回原本的元素。
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasAnySelection))]
    void Copy()
    {
        var shapes = Selection.Where(e => e.HasGeometry).ToList();
        var ids = shapes.Select(e => e.Id).ToHashSet();
        var conveyors = Elements.Where(e => e.IsConveyor && (e.IsSelected || ids.Contains(e.From ?? "") || ids.Contains(e.To ?? ""))).ToList();
        _clipboard = PlantConfigFile.SerializeElements(shapes.Concat(conveyors).Select(e => e.ToModel()));
        Status = $"已複製 {shapes.Count + conveyors.Count} 個元素。";
    }

    /// <summary>
    /// 貼上（Ctrl+V）：產生新的 id，名稱、尺寸、旋轉、自訂屬性與別名照抄（別名重複由存檔前檢查擋下）。
    /// 以 (x, y)（公尺，滑鼠目前位置）當作整組的左上角；滑鼠不在畫布上（null）時放在原位置往右下 1 公尺。貼上後選取新元素。
    /// </summary>
    public void Paste(double? x, double? y)
    {
        if (_clipboard is null)
        {
            Status = "剪貼簿是空的：先選取元素再按 Ctrl+C。";
            return;
        }
        var items = PlantConfigFile.DeserializeElements(_clipboard);
        var shapes = items.Where(e => e.Type != LayoutElementType.Conveyor).ToList();
        double dx = 1, dy = 1;
        if (shapes.Count == 0) (dx, dy) = (0, 0);
        else if (x is { } px && y is { } py)
        {
            var bounds = shapes.Select(e => LayoutGeometry.Bounds(e.X ?? 0, e.Y ?? 0, e.W ?? 0, e.H ?? 0, e.Rotation ?? 0)).ToList();
            dx = Math.Max(0, SnapTo(px)) - bounds.Min(b => b.Left);
            dy = Math.Max(0, SnapTo(py)) - bounds.Min(b => b.Top);
        }
        LayoutPoint? Shift(LayoutPoint? p) => p is null ? null : new(p.X + dx, p.Y + dy);
        var map = new Dictionary<string, string>();
        var added = new List<LayoutElementViewModel>();
        Batch(() =>
        {
            foreach (var e in shapes)
            {
                map[e.Id] = e.Id = NextId(e.Type);
                (e.X, e.Y) = (e.X + dx, e.Y + dy);
                added.Add(AttachNew(e));
            }
            foreach (var c in items.Where(e => e.Type == LayoutElementType.Conveyor))
            {
                // 端點在剪貼簿中時改連到新元素，交接點跟著平移；否則連回原本的元素
                if (c.From is { } f && map.TryGetValue(f, out var nf)) (c.From, c.FromPoint) = (nf, Shift(c.FromPoint));
                if (c.To is { } t && map.TryGetValue(t, out var nt)) (c.To, c.ToPoint) = (nt, Shift(c.ToPoint));
                if (Find(c.From) is null || Find(c.To) is null) continue; // 原本的元素已經刪除
                c.Id = NextId(LayoutElementType.Conveyor);
                added.Add(AttachNew(c));
            }
        });
        IsDirty = true;
        SetSelection(added);
        Status = $"已貼上 {added.Count} 個元素；別名照抄，重複的別名存檔前會提示。";
    }

    LayoutElementViewModel AttachNew(LayoutElement model)
    {
        var vm = new LayoutElementViewModel(model);
        Attach(vm);
        return vm;
    }

    // ---- 復原（Ctrl+Z）：只包含 Layout 元素；最多 50 步，存檔或關閉後清空 ----

    public const int MaxUndoSteps = 50;

    readonly List<string> _undo = [];
    string _undoBaseline = "[]";
    string? _lastEditKey;
    int _suppressUndo;

    public int UndoCount => _undo.Count;

    string Snapshot() => PlantConfigFile.SerializeElements(Elements.Select(e => e.ToModel()));

    /// <summary>
    /// 記錄一次修改：把修改前的狀態放進復原堆疊。key 相同的連續修改（同一欄位連續輸入）合併成一步；
    /// 拖曳中不記錄，放開時記成一步。
    /// </summary>
    void Commit(string? key)
    {
        if (_suppressUndo > 0 || _drag is not null || _pointDrag is not null) return;
        var now = Snapshot();
        if (now == _undoBaseline) return;
        if (key is null || key != _lastEditKey)
        {
            _undo.Add(_undoBaseline);
            if (_undo.Count > MaxUndoSteps) _undo.RemoveAt(0);
            UndoCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(UndoCount));
        }
        _undoBaseline = now;
        _lastEditKey = key;
    }

    /// <summary>多個變更算一步。</summary>
    void Batch(Action action)
    {
        _suppressUndo++;
        try
        {
            action();
        }
        finally
        {
            _suppressUndo--;
        }
        Commit(null);
    }

    void ResetUndo()
    {
        _undo.Clear();
        _undoBaseline = Snapshot();
        _lastEditKey = null;
        UndoCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(UndoCount));
    }

    bool CanUndo() => _undo.Count > 0;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    void Undo()
    {
        var state = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        var selected = Selection.Select(e => e.Id).ToHashSet();
        SetSelection([]);
        _suppressUndo++;
        try
        {
            foreach (var e in Elements) Detach(e);
            Elements.Clear();
            foreach (var m in PlantConfigFile.DeserializeElements(state)) Attach(new LayoutElementViewModel(m));
        }
        finally
        {
            _suppressUndo--;
        }
        _undoBaseline = state;
        _lastEditKey = null;
        SetSelection(Elements.Where(e => selected.Contains(e.Id)));
        IsDirty = true;
        UndoCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(UndoCount));
        Status = _undo.Count > 0 ? $"已復原；還可以復原 {_undo.Count} 步。" : "已復原；沒有可以再復原的步驟。";
    }

    public static double SnapTo(double value) => Math.Round(value / Snap, MidpointRounding.AwayFromZero) * Snap;

    /// <summary>
    /// 開始拖曳（移動或調整大小）：記下元素原本的位置與尺寸。拖曳已選取的元素之一時，所有選取的元素一起移動；
    /// 拖曳沒有選取的元素時，改為只選取它。
    /// </summary>
    public void BeginDrag(LayoutElementViewModel element)
    {
        if (!element.IsSelected) SelectedElement = element;
        IReadOnlyList<LayoutElementViewModel> group = SelectionCount > 1 ? Selection : [element];
        _drag = group.Where(e => e.HasGeometry).Select(e => (e, e.X, e.Y, e.W, e.H)).ToList();
        _dragPrimary = element;
    }

    /// <summary>移動：從開始拖曳的位置加上位移（公尺），吸附到格線，整組不超出畫布左上角。</summary>
    public void DragMoveBy(double dx, double dy)
    {
        if (_drag is not { Count: > 0 } group) return;
        dx = Math.Max(dx, -group.Min(d => d.X));
        dy = Math.Max(dy, -group.Min(d => d.Y));
        foreach (var d in group)
        {
            d.Element.X = Math.Max(0, SnapTo(d.X + dx));
            d.Element.Y = Math.Max(0, SnapTo(d.Y + dy));
        }
    }

    /// <summary>
    /// 調整大小：從開始拖曳的尺寸加上位移（公尺），吸附到格線，最小一格。
    /// 元素旋轉 90 或 270 度時，畫面上的橫向對應元素的深（H）。
    /// </summary>
    public void DragResizeBy(double dx, double dy)
    {
        if (_drag?.FirstOrDefault(x => x.Element == _dragPrimary) is not { Element: not null } d) return;
        if (d.Element.Rotation is 90 or 270) (dx, dy) = (dy, dx);
        d.Element.W = Math.Max(Snap, SnapTo(d.W + dx));
        d.Element.H = Math.Max(Snap, SnapTo(d.H + dy));
    }

    public void EndDrag()
    {
        _drag = null;
        _dragPrimary = null;
        _pointDrag = null;
        Commit(null); // 整個拖曳算一步
    }

    /// <summary>開始拖曳輸送線的交接點（連到搬送機的那一端）。</summary>
    public void BeginTransferPointDrag(LayoutElementViewModel conveyor, bool from)
    {
        SelectedElement = conveyor;
        if ((from ? conveyor.FromPoint : conveyor.ToPoint) is not null) _pointDrag = (conveyor, from);
    }

    /// <summary>把交接點移到 (x, y)（公尺），吸附到格線，不小於 0。</summary>
    public void DragTransferPointTo(double x, double y)
    {
        if (_pointDrag is not { } d) return;
        var p = new LayoutPoint(Math.Max(0, SnapTo(x)), Math.Max(0, SnapTo(y)));
        if (d.From) d.Conveyor.FromPoint = p;
        else d.Conveyor.ToPoint = p;
    }

    /// <summary>旋轉到指定角度（只允許 0、90、180、270）。</summary>
    [RelayCommand(CanExecute = nameof(CanEditSelection))]
    void SetRotation(int angle)
    {
        if (SelectedElement is not { HasGeometry: true } e) return;
        if (!Rotations.Contains(angle))
        {
            Status = $"旋轉只能是 0、90、180、270 度。";
            return;
        }
        e.Rotation = angle;
    }

    /// <summary>順時針轉 90 度。</summary>
    [RelayCommand(CanExecute = nameof(CanEditSelection))]
    void RotateClockwise()
    {
        if (SelectedElement is { HasGeometry: true } e) e.Rotation = (e.Rotation + 90) % 360;
    }

    // ---- 別名與自訂屬性 ----

    /// <summary>目前擁有這個別名的元素（大小寫視為相同，ADR-0006 第 2 點）。</summary>
    public LayoutElementViewModel? AliasOwner(string alias) =>
        Elements.FirstOrDefault(e => e.Aliases.Any(a => string.Equals(a, alias.Trim(), StringComparison.OrdinalIgnoreCase)));

    /// <summary>為選取的元素加上別名；別名已經屬於其他元素時擋下，並指出是哪一個。</summary>
    [RelayCommand(CanExecute = nameof(CanEditSelection))]
    void AddAlias()
    {
        if (TryAddAlias(SelectedElement!, NewAlias)) NewAlias = "";
    }

    bool TryAddAlias(LayoutElementViewModel e, string text)
    {
        var alias = text.Trim();
        if (alias.Length == 0) return false;
        if (!e.CanHaveAliases)
        {
            Status = $"「{e.TypeLabel}」不能加別名；只有站點、搬送機、對接設備可以。";
            return false;
        }
        var owner = AliasOwner(alias);
        if (owner == e)
        {
            Status = $"「{e.Name}」已經有別名「{alias}」。";
            return false;
        }
        if (owner is not null)
        {
            Status = $"別名「{alias}」已經屬於「{owner.Name}」。同一個別名只能屬於一個元素。";
            return false;
        }
        e.Aliases.Add(alias);
        Status = $"已把「{alias}」加為「{e.Name}」的別名。";
        return true;
    }

    // ---- 載入案件比對與未對應的編號（ADR-0006 第 3 點） ----

    /// <summary>可以比對的案件。</summary>
    public ObservableCollection<CaseSummary> CaseChoices { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadCaseCompareCommand))]
    public partial CaseSummary? SelectedCompareCase { get; set; }

    /// <summary>目前比對的案件名稱；沒有比對時為 null。</summary>
    [ObservableProperty]
    public partial string? CompareCaseName { get; private set; }

    /// <summary>案件中出現、但 Layout 中還沒有任何元素以它為別名的站點、搬送機、對接設備編號，依出現次數排序。</summary>
    public ObservableCollection<UnmappedIdViewModel> Unmapped { get; } = [];

    public void RefreshCaseChoices()
    {
        var selected = SelectedCompareCase?.Folder;
        CaseChoices.Clear();
        foreach (var c in _cases?.List() ?? []) CaseChoices.Add(c);
        SelectedCompareCase = CaseChoices.FirstOrDefault(c => c.Folder == selected);
    }

    bool CanLoadCaseCompare() => SelectedCompareCase is not null;

    /// <summary>讀出案件中出現的編號與次數（不修改案件），與目前的別名比對。</summary>
    [RelayCommand(CanExecute = nameof(CanLoadCaseCompare))]
    void LoadCaseCompare()
    {
        var summary = SelectedCompareCase!;
        try
        {
            using var c = Case.Open(summary.Folder);
            _caseIds = c.GetIdentifierCounts();
            CompareCaseName = summary.Name;
            RefreshUnmapped();
            Status = $"已載入案件「{summary.Name}」比對：{Unmapped.Count} 個編號還沒對應。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
        {
            Status = "無法載入案件：" + ex.Message;
        }
    }

    void RefreshUnmapped()
    {
        var aliases = Elements.SelectMany(e => e.Aliases).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = _caseIds.Where(x => !aliases.Contains(x.Value))
            .GroupBy(x => x.Value, StringComparer.OrdinalIgnoreCase)
            .Select(g => new UnmappedIdViewModel(g.First().Kind, g.First().Value, g.Max(x => x.Count)))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Value, StringComparer.Ordinal)
            .ToList();
        if (list.SequenceEqual(Unmapped)) return;
        Unmapped.Clear();
        foreach (var x in list) Unmapped.Add(x);
    }

    /// <summary>把未對應的編號拖到元素上：加為該元素的別名（與手動加入別名相同的檢查）。</summary>
    public bool AssignUnmapped(UnmappedIdViewModel id, LayoutElementViewModel target)
    {
        SelectedElement = target;
        return TryAddAlias(target, id.Value);
    }

    // ---- 解析規則（ADR-0005 第 8 點） ----

    public ObservableCollection<ParseRuleViewModel> ParseRules { get; } = [];

    public IReadOnlyList<string> EffectChoices => ParseRuleViewModel.EffectChoices;

    /// <summary>代表「在席」的訊號名稱：效果為 signal 的規則抽到這個訊號時，決定站點的在席燈（ADR-0005 第 3 點）。</summary>
    [ObservableProperty]
    public partial string PresenceSignal { get; set; } = "";

    partial void OnPresenceSignalChanged(string value) => IsDirty = true;

    /// <summary>站點模式代號的名稱（ADR-0005 第 3 點）；回放畫面顯示名稱，沒填的代號直接顯示。</summary>
    public ObservableCollection<StationModeNameViewModel> StationModeNames { get; } = [];

    void AddModeName(StationModeNameViewModel row)
    {
        row.PropertyChanged += (_, _) => IsDirty = true;
        StationModeNames.Add(row);
    }

    [RelayCommand]
    void AddStationModeName()
    {
        AddModeName(new StationModeNameViewModel("", ""));
        IsDirty = true;
    }

    [RelayCommand]
    void RemoveStationModeName(StationModeNameViewModel? row)
    {
        if (row is not null && StationModeNames.Remove(row)) IsDirty = true;
    }

    /// <summary>沒有 LOG 的門檻（ADR-0006、#84）：模組、秒數；沒有列出的模組 300 秒，0 表示不標示。</summary>
    public ObservableCollection<GapThresholdViewModel> GapThresholds { get; } = [];

    void AddGapThreshold(GapThresholdViewModel row)
    {
        row.PropertyChanged += (_, _) => IsDirty = true;
        GapThresholds.Add(row);
    }

    [RelayCommand]
    void AddGapThresholdRow()
    {
        AddGapThreshold(new GapThresholdViewModel("", LogTrace.Engine.Events.LogGaps.DefaultThresholdSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        IsDirty = true;
    }

    [RelayCommand]
    void RemoveGapThreshold(GapThresholdViewModel? row)
    {
        if (row is not null && GapThresholds.Remove(row)) IsDirty = true;
    }

    void AddParseRule(ParseRuleViewModel rule)
    {
        rule.PropertyChanged += (_, _) => IsDirty = true;
        ParseRules.Add(rule);
    }

    [RelayCommand]
    void NewParseRule()
    {
        AddParseRule(new ParseRuleViewModel($"規則 {ParseRules.Count + 1}", "", "", ""));
        IsDirty = true;
    }

    [RelayCommand]
    void RemoveParseRule(ParseRuleViewModel? rule)
    {
        if (rule is not null && ParseRules.Remove(rule)) IsDirty = true;
    }

    /// <summary>還原成初始規則（依 docs/log-format-notes.md 的樣本格式）。</summary>
    [RelayCommand]
    void ResetParseRules()
    {
        ParseRules.Clear();
        foreach (var r in PlantConfig.DefaultParseRules) AddParseRule(ParseRuleViewModel.From(r));
        IsDirty = true;
    }

    [RelayCommand]
    void RemoveAlias(string? alias)
    {
        if (SelectedElement is { } e && alias is not null) e.Aliases.Remove(alias);
    }

    [RelayCommand(CanExecute = nameof(CanEditSelection))]
    void AddProperty()
    {
        var e = SelectedElement!;
        int n = 1;
        while (e.Props.Any(p => p.Key == $"屬性{n}")) n++;
        var item = new PropertyItemViewModel($"屬性{n}", "");
        WatchProperty(item);
        e.Props.Add(item);
    }

    [RelayCommand]
    void RemoveProperty(PropertyItemViewModel? item)
    {
        if (SelectedElement is { } e && item is not null) e.Props.Remove(item);
    }
}
