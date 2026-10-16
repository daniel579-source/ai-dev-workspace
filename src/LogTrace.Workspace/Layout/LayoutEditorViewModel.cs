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
    (LayoutElementViewModel Element, double X, double Y, double W, double H)? _drag;

    /// <param name="cases">「載入案件比對」可以選的案件；null 時不提供。</param>
    public LayoutEditorViewModel(PlantConfigLibrary library, IUserInteraction ui, CaseLibrary? cases = null)
    {
        _library = library;
        _ui = ui;
        _cases = cases;
        Elements.CollectionChanged += (_, _) =>
        {
            RefreshEndpoints();
            RefreshUnmapped();
        };
        RefreshConfigs();
        RefreshCaseChoices();
        Load(new PlantConfigDocument { Plant = "新廠別" }, null);
        Status = "建立新的廠別設定，或從清單開啟既有的設定。";
    }

    public IReadOnlyList<LayoutElementType> AddableTypes => LayoutElementTypes.Addable;

    /// <summary>存放位置下的廠別設定檔。</summary>
    public ObservableCollection<string> Configs { get; } = [];

    public ObservableCollection<LayoutElementViewModel> Elements { get; } = [];

    /// <summary>輸送線兩端可以選的元素（站點與對接設備）。</summary>
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

    /// <summary>屬性面板中輸入、準備加入的別名。</summary>
    [ObservableProperty]
    public partial string NewAlias { get; set; } = "";

    public string Title => $"Layout 編輯器 - {(FilePath is null ? "（尚未存檔）" : Path.GetFileName(FilePath))}{(IsDirty ? " *" : "")}";

    partial void OnSelectedElementChanged(LayoutElementViewModel? oldValue, LayoutElementViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
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
        foreach (var e in Elements) Detach(e);
        _document = doc;
        SelectedElement = null;
        Elements.Clear();
        foreach (var e in doc.Layout.Elements) Attach(new LayoutElementViewModel(e));
        PlantName = doc.Plant;
        StorageCodeRule = doc.Layout.StorageCodeRule ?? "";
        ModuleOrderText = string.Join(Environment.NewLine, doc.ModuleOrder);
        KnownLoggersText = string.Join(Environment.NewLine, doc.KnownLoggers);
        EncodingRules.Clear();
        foreach (var r in doc.Encodings) AddRule(EncodingRuleViewModel.From(r));
        ParseRules.Clear();
        foreach (var r in doc.ParseRules) AddParseRule(ParseRuleViewModel.From(r));
        PresenceSignal = doc.PresenceSignal;
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
        foreach (var p in e.Props) p.PropertyChanged += (_, _) => IsDirty = true;
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
    }

    void OnElementChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LayoutElementViewModel.HasIssue) or nameof(LayoutElementViewModel.IsSelected)
            or nameof(LayoutElementViewModel.AliasesText) or nameof(LayoutElementViewModel.LineX1) or nameof(LayoutElementViewModel.LineY1)
            or nameof(LayoutElementViewModel.LineX2) or nameof(LayoutElementViewModel.LineY2)
            or nameof(LayoutElementViewModel.CenterX) or nameof(LayoutElementViewModel.CenterY))
            return;
        IsDirty = true;
        if (e.PropertyName is nameof(LayoutElementViewModel.X) or nameof(LayoutElementViewModel.Y) or nameof(LayoutElementViewModel.W)
            or nameof(LayoutElementViewModel.H) or nameof(LayoutElementViewModel.From) or nameof(LayoutElementViewModel.To)
            or nameof(LayoutElementViewModel.Name))
            RefreshEndpoints();
    }

    /// <summary>更新輸送線兩端的位置，以及可以當作輸送線兩端的元素。</summary>
    void RefreshEndpoints()
    {
        var byId = Elements.Where(e => e.HasGeometry).ToDictionary(e => e.Id);
        foreach (var c in Elements.Where(e => e.IsConveyor))
        {
            if (c.From is { } f && byId.TryGetValue(f, out var a)) (c.LineX1, c.LineY1) = (a.CenterX, a.CenterY);
            if (c.To is { } t && byId.TryGetValue(t, out var b)) (c.LineX2, c.LineY2) = (b.CenterX, b.CenterY);
        }
        var endpoints = Elements.Where(e => e.Type is LayoutElementType.Station or LayoutElementType.Dock).ToList();
        if (!endpoints.SequenceEqual(ConveyorEndpoints))
        {
            ConveyorEndpoints.Clear();
            foreach (var e in endpoints) ConveyorEndpoints.Add(e);
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
            var ends = ConveyorEndpoints.ToList();
            if (ends.Count < 2)
            {
                Status = "輸送線需要至少兩個站點或對接設備。";
                return;
            }
            model = new LayoutElement { Id = id, Type = type, Name = $"輸送線 {id[(id.IndexOf('-') + 1)..]}", From = ends[0].Id, To = ends[1].Id };
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

    /// <summary>刪除選取的元素；連到它的輸送線一起刪除。</summary>
    [RelayCommand(CanExecute = nameof(CanEditSelection))]
    void DeleteSelected()
    {
        var e = SelectedElement!;
        var removed = Elements.Where(x => x == e || (x.IsConveyor && (x.From == e.Id || x.To == e.Id))).ToList();
        SelectedElement = null;
        foreach (var x in removed)
        {
            Detach(x);
            Elements.Remove(x);
        }
        IsDirty = true;
        Status = removed.Count > 1 ? $"已刪除「{e.Name}」與連到它的 {removed.Count - 1} 條輸送線。" : $"已刪除「{e.Name}」。";
    }

    public static double SnapTo(double value) => Math.Round(value / Snap, MidpointRounding.AwayFromZero) * Snap;

    /// <summary>開始拖曳（移動或調整大小）：記下元素原本的位置與尺寸。</summary>
    public void BeginDrag(LayoutElementViewModel element)
    {
        SelectedElement = element;
        _drag = (element, element.X, element.Y, element.W, element.H);
    }

    /// <summary>移動：從開始拖曳的位置加上位移（公尺），吸附到格線，不超出畫布左上角。</summary>
    public void DragMoveBy(double dx, double dy)
    {
        if (_drag is not { } d || !d.Element.HasGeometry) return;
        d.Element.X = Math.Max(0, SnapTo(d.X + dx));
        d.Element.Y = Math.Max(0, SnapTo(d.Y + dy));
    }

    /// <summary>
    /// 調整大小：從開始拖曳的尺寸加上位移（公尺），吸附到格線，最小一格。
    /// 元素旋轉 90 或 270 度時，畫面上的橫向對應元素的深（H）。
    /// </summary>
    public void DragResizeBy(double dx, double dy)
    {
        if (_drag is not { } d || !d.Element.HasGeometry) return;
        if (d.Element.Rotation is 90 or 270) (dx, dy) = (dy, dx);
        d.Element.W = Math.Max(Snap, SnapTo(d.W + dx));
        d.Element.H = Math.Max(Snap, SnapTo(d.H + dy));
    }

    public void EndDrag() => _drag = null;

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
        item.PropertyChanged += (_, _) => IsDirty = true;
        e.Props.Add(item);
    }

    [RelayCommand]
    void RemoveProperty(PropertyItemViewModel? item)
    {
        if (SelectedElement is { } e && item is not null) e.Props.Remove(item);
    }
}
