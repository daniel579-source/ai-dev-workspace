using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogTrace.Engine.Events;
using LogTrace.Engine.Playback;
using LogTrace.Engine.Plants;
using LogTrace.Workspace.Layout;

namespace LogTrace.Workspace.Playback;

/// <summary>元素在 Layout 面板上的外觀（ADR-0008 第 6 點的圖例）。</summary>
public enum ElementLook
{
    /// <summary>沒有任何事件確定過狀態：灰色。</summary>
    Unknown,
    /// <summary>狀態已知、沒有貨物。</summary>
    Empty,
    /// <summary>有貨物。</summary>
    Occupied,
    /// <summary>搬送機執行中。</summary>
    Executing,
    /// <summary>異常中（有未復歸的警報）。</summary>
    Alarm,
    /// <summary>輔助元素（區域、文字、輸送線），不推導狀態。</summary>
    Auxiliary,
}

/// <summary>設備狀態表的排序欄位。</summary>
public enum EquipmentSort
{
    Name,
    Type,
    State,
    Abnormal,
}

/// <summary>設備狀態表的一列（ADR-0008 第 6 點）：站點、搬送機、對接設備在目前時刻的狀態。</summary>
/// <param name="IsAbnormal">異常中（有未復歸的警報）或不一致。</param>
public sealed record EquipmentRowViewModel(string Id, string Name, string TypeLabel, string Aliases, string State, bool IsAbnormal, ElementLook Look);

/// <summary>詳細資訊的一列：狀態，以及造成這個狀態的事件（可以跳過去）。</summary>
public sealed record StateLineViewModel(string Label, string Value, long? Cause, string? CauseText);

/// <summary>料架的一列（沿長邊切成的小格）；以有貨的層數表示深淺（ADR-0006 第 4 點）。</summary>
public sealed partial class RackCellViewModel(int column, int levels) : ObservableObject
{
    public int Column { get; } = column;
    public int Levels { get; } = levels;

    [ObservableProperty]
    public partial int Occupied { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>料架側視圖的一層。</summary>
public sealed record RackLevelViewModel(int Level, string? StorageCode, string? Pallet, long? Cause, string? CauseText);

/// <summary>Layout 面板上的一個元素與它在目前時刻的狀態。</summary>
public sealed partial class PlaybackElementViewModel : ObservableObject
{
    public PlaybackElementViewModel(LayoutElement e, IReadOnlyDictionary<string, LayoutElement> byId)
    {
        Model = e;
        (X, Y, W, H, Rotation) = (e.X ?? 0, e.Y ?? 0, e.W ?? 0, e.H ?? 0, e.Rotation ?? 0);
        if (e.Type == LayoutElementType.Conveyor && e.From is { } f && e.To is { } t && byId.TryGetValue(f, out var a) && byId.TryGetValue(t, out var b))
            ((LineX1, LineY1), (LineX2, LineY2)) = (End(a, e.FromPoint), End(b, e.ToPoint));
        if (e.Type == LayoutElementType.Rack && e.ColFrom is { } from && e.ColTo is { } to && to >= from)
            for (int c = from; c <= to; c++) Cells.Add(new RackCellViewModel(c, e.Levels ?? 0));
        Look = IsAuxiliary ? ElementLook.Auxiliary : ElementLook.Unknown;
    }

    /// <summary>輸送線的一端：連到搬送機時畫到交接點，其他畫到元素中心（ADR-0006 第 7 點）。</summary>
    static (double X, double Y) End(LayoutElement e, LayoutPoint? point) =>
        e.Type == LayoutElementType.Vehicle && point is not null ? (point.X, point.Y) : Center(e);

    static (double X, double Y) Center(LayoutElement e) => ((e.X ?? 0) + (e.W ?? 0) / 2, (e.Y ?? 0) + (e.H ?? 0) / 2);

    public LayoutElement Model { get; }
    public string Id => Model.Id;
    public LayoutElementType Type => Model.Type;
    public string Name => Model.Name ?? Model.Id;
    public string TypeLabel => LayoutElementTypes.Label(Model.Type);
    public string Aliases => string.Join("・", Model.Aliases ?? []);
    public bool IsConveyor => Type == LayoutElementType.Conveyor;
    public bool IsRack => Type == LayoutElementType.Rack;
    public bool HasGeometry => !IsConveyor;
    public bool IsAuxiliary => Type is LayoutElementType.Conveyor or LayoutElementType.Area or LayoutElementType.Label;

    public double X { get; }
    public double Y { get; }
    public double W { get; }
    public double H { get; }
    public int Rotation { get; }
    public double LineX1 { get; }
    public double LineY1 { get; }
    public double LineX2 { get; }
    public double LineY2 { get; }

    /// <summary>料架的各列。</summary>
    public ObservableCollection<RackCellViewModel> Cells { get; } = [];

    [ObservableProperty]
    public partial ElementLook Look { get; set; }

    /// <summary>站點右上角的在席指示燈：●＝ON、○＝OFF、◌＝未知（ADR-0005 第 3 點）；其他元素為 null。</summary>
    [ObservableProperty]
    public partial string? PresenceLight { get; set; }

    /// <summary>
    /// 站點的運轉狀態不是「自動」時顯示的小標記（運轉狀態的第一個字，ADR-0005 第 3 點）；未知、自動或其他元素為 null。
    /// </summary>
    [ObservableProperty]
    public partial string? StatusBadge { get; set; }

    /// <summary>命令與在席訊號矛盾（ADR-0005 第 3 點）。</summary>
    [ObservableProperty]
    public partial bool IsInconsistent { get; set; }

    /// <summary>元素上的簡短狀態（例如棧板編號、搬送機的命令）。</summary>
    [ObservableProperty]
    public partial string? Summary { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>
/// Layout 回放（ADR-0008 第 6 點）：Layout 面板、選取元素的詳細資訊（含造成狀態的事件）、料架側視圖。
/// 目前時刻由主畫面設定（事件清單選取的事件）；推不出狀態時顯示「未知」，不顯示猜測值。
/// </summary>
public sealed partial class PlaybackViewModel : ObservableObject
{
    WorldModel? _model;

    /// <summary>世界模型；沒有 Layout 時為 null。</summary>
    public WorldModel? Model => _model;

    /// <summary>目前時刻的世界狀態。</summary>
    public WorldState State => _state;
    IReadOnlyDictionary<long, LogEvent> _events = new Dictionary<long, LogEvent>();
    WorldState _state = new();
    string? _storageRule;

    /// <summary>點「造成這個狀態的事件」時呼叫：主畫面選取該事件。</summary>
    public Action<long>? GoToEvent { get; set; }

    public ObservableCollection<PlaybackElementViewModel> Elements { get; } = [];

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

    /// <summary>沒有 Layout 或沒有狀態規則時的說明；可以回放時為 null。</summary>
    [ObservableProperty]
    public partial string? Message { get; private set; } = "請開啟案件。";

    /// <summary>目前時刻落在某些模組沒有 LOG 的時段時的提示（#84，由主畫面設定）；沒有時為 null。</summary>
    [ObservableProperty]
    public partial string? GapNotice { get; set; }

    /// <summary>目前時刻（顯示用）。</summary>
    [ObservableProperty]
    public partial string? MomentText { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial PlaybackElementViewModel? SelectedElement { get; set; }

    public bool HasSelection => SelectedElement is not null;

    /// <summary>選取元素在目前時刻的狀態，每一列附上造成它的事件。</summary>
    public ObservableCollection<StateLineViewModel> Detail { get; } = [];

    /// <summary>料架側視圖：選取的列，逐層列出儲位與棧板。</summary>
    public ObservableCollection<RackLevelViewModel> SideView { get; } = [];

    [ObservableProperty]
    public partial string? SideViewTitle { get; private set; }

    RackCellViewModel? _selectedCell;

    public void Clear(string message)
    {
        _model = null;
        _state = new();
        Equipment.Clear();
        Elements.Clear();
        Detail.Clear();
        SideView.Clear();
        SelectedElement = null;
        SideViewTitle = null;
        MomentText = null;
        Message = message;
    }

    /// <summary>載入 Layout 與世界模型；events 用來顯示造成狀態的事件。</summary>
    public void Load(WorldModel model, LayoutSection layout, IReadOnlyList<LogEvent> events, bool hasStateRules,
        IReadOnlyList<KeyValuePair<string, string>>? stationModeNames = null)
    {
        _model = model;
        _stationModeNames = (stationModeNames ?? []).GroupBy(p => p.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
        _storageRule = layout.StorageCodeRule;
        _events = events.ToDictionary(e => e.Sequence);
        var byId = layout.Elements.ToDictionary(e => e.Id, StringComparer.Ordinal);
        Elements.Clear();
        foreach (var e in layout.Elements) Elements.Add(new PlaybackElementViewModel(e, byId));
        (CanvasWidth, CanvasHeight) = LayoutGeometry.CanvasSize(layout.Elements);
        SelectedElement = null;
        Message = hasStateRules ? null : "廠別設定的解析規則中，只有在席等少數狀態變化；命令、搬送機、警報等需要在規則中設定「效果」才能推導。";
        SetMoment(0);
    }

    /// <summary>目前時刻：事件序號 sequence（含）之前的所有事件套用後的狀態。</summary>
    public void SetMoment(long sequence)
    {
        if (_model is null) return;
        _state = _model.StateAt(sequence);
        MomentText = sequence == 0 || !_events.TryGetValue(sequence, out var e)
            ? "案件開始（所有元素都是未知）"
            : $"{e.Timestamp:yyyy-MM-dd HH:mm:ss.ffff}　事件 #{sequence}";
        foreach (var el in Elements) Update(el);
        UpdateDetail();
        UpdateSideView();
        UpdateEquipment();
    }

    partial void OnSelectedElementChanged(PlaybackElementViewModel? oldValue, PlaybackElementViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        if (newValue is null || !newValue.IsRack) SelectRackCell(null);
        UpdateDetail();
    }

    void Update(PlaybackElementViewModel el)
    {
        var w = _state;
        switch (el.Type)
        {
            case LayoutElementType.Station:
            {
                w.Stations.TryGetValue(el.Id, out var s);
                var pallets = w.PalletsAt(LocationKind.Station, el.Id).Select(p => p.Id).ToList();
                el.PresenceLight = s?.Presence switch { true => "●", false => "○", null => "◌" };
                el.StatusBadge = s?.Status is { Length: > 0 } status && status != NormalStationStatus ? status[..1] : null;
                el.IsInconsistent = w.IsInconsistent(el.Id);
                el.Summary = pallets.Count > 0 ? string.Join("、", pallets) : null;
                el.Look = w.Alarms.ContainsKey(el.Id) ? ElementLook.Alarm
                    : pallets.Count > 0 ? ElementLook.Occupied
                    : s is null ? ElementLook.Unknown : ElementLook.Empty;
                break;
            }
            case LayoutElementType.Vehicle:
            {
                w.Vehicles.TryGetValue(el.Id, out var v);
                var load = w.PalletsAt(LocationKind.Vehicle, el.Id).Select(p => p.Id).ToList();
                el.Summary = v?.Work == VehicleWork.Executing ? $"{v.Command} → {v.Destination}" : load.Count > 0 ? string.Join("、", load) : null;
                el.Look = w.Alarms.ContainsKey(el.Id) ? ElementLook.Alarm
                    : v?.Work == VehicleWork.Executing ? ElementLook.Executing
                    : v is null ? ElementLook.Unknown : ElementLook.Empty;
                break;
            }
            case LayoutElementType.Dock:
            {
                var delivered = w.PalletsAt(LocationKind.Delivered, el.Id).Count();
                el.Summary = delivered > 0 ? $"已交出 {delivered}" : null;
                el.Look = w.Alarms.ContainsKey(el.Id) ? ElementLook.Alarm : delivered > 0 ? ElementLook.Empty : ElementLook.Unknown;
                break;
            }
            case LayoutElementType.Rack:
            {
                int total = 0;
                foreach (var cell in el.Cells)
                {
                    cell.Occupied = Enumerable.Range(1, cell.Levels)
                        .Count(l => StorageCode.Format(_storageRule, el.Model.Row ?? "", cell.Column, l) is { } code && w.Storage.ContainsKey(code));
                    total += cell.Occupied;
                }
                el.Summary = total > 0 ? $"{total} 個儲位有貨" : null;
                el.Look = total > 0 ? ElementLook.Occupied : ElementLook.Unknown;
                break;
            }
        }
    }

    Dictionary<string, string> _stationModeNames = [];

    /// <summary>運轉狀態的正常值：不是這個值時在 Layout 上加小標記（以文字比對，ADR-0005 第 3 點）。</summary>
    public const string NormalStationStatus = "自動";

    /// <summary>站點模式：廠別設定有名稱時顯示名稱，沒有時直接顯示代號（ADR-0005 第 3 點）。</summary>
    public string StationModeText(string? code) =>
        code is null ? "未知" : _stationModeNames.TryGetValue(code, out var name) ? name : code;

    StateLineViewModel Line(string label, string value, long? cause) =>
        new(label, value, cause, cause is { } c && _events.TryGetValue(c, out var e) ? $"#{c} {e.Timestamp:HH:mm:ss.ffff} {e.Message.Split('\n')[0]}" : null);

    void UpdateDetail()
    {
        Detail.Clear();
        if (SelectedElement is not { } el || _model is null) return;
        var w = _state;
        switch (el.Type)
        {
            case LayoutElementType.Station:
            {
                w.Stations.TryGetValue(el.Id, out var s);
                Detail.Add(Line("在席", s?.Presence switch { true => "ON", false => "OFF", null => "未知" }, s?.PresenceCause));
                Detail.Add(Line("站點模式", StationModeText(s?.Mode), s?.ModeCause));
                Detail.Add(Line("運轉狀態", s?.Status ?? "未知", s?.StatusCause));
                foreach (var (name, (on, cause)) in s?.Signals ?? []) Detail.Add(Line(name, on ? "ON" : "OFF", cause));
                foreach (var p in w.PalletsAt(LocationKind.Station, el.Id))
                    Detail.Add(Line("貨物", PalletText(p), p.LocationCause));
                if (w.IsInconsistent(el.Id))
                    Detail.Add(Line("不一致", "命令說貨物在這個站點，但在席是 OFF（不判斷哪一邊才對）", s?.PresenceCause));
                break;
            }
            case LayoutElementType.Vehicle:
            {
                w.Vehicles.TryGetValue(el.Id, out var v);
                Detail.Add(Line("連線", v?.Connection ?? "未知", v?.ConnectionCause));
                Detail.Add(Line("模式", v?.Connection == "OFF-LINE" ? "（OFF-LINE 時沒有意義）" : v?.Mode ?? "未知", v?.ModeCause));
                Detail.Add(Line("作業", v?.Work switch
                {
                    VehicleWork.Executing => $"執行中：命令 {v.Command} → {v.Destination}",
                    VehicleWork.Idle => "閒置",
                    _ => "未知",
                }, v?.WorkCause));
                foreach (var p in w.PalletsAt(LocationKind.Vehicle, el.Id))
                    Detail.Add(Line("載著", PalletText(p), p.LocationCause));
                break;
            }
            case LayoutElementType.Dock:
                foreach (var p in w.PalletsAt(LocationKind.Delivered, el.Id))
                    Detail.Add(Line("已交出", PalletText(p), p.LocationCause));
                if (Detail.Count == 0) Detail.Add(Line("已交出", "無", null));
                break;
            case LayoutElementType.Rack:
                Detail.Add(Line("料架", $"排 {el.Model.Row}，列 {el.Model.ColFrom}～{el.Model.ColTo}，{el.Model.Levels} 層；點一列看側視圖", null));
                break;
            default:
                Detail.Add(Line(el.TypeLabel, "輔助元素，不推導狀態", null));
                break;
        }
        if (w.Alarms.TryGetValue(el.Id, out var alarm))
            Detail.Add(Line("異常中", $"警報 {alarm.Code}" + (_model.IsAlarmNeverReset(el.Id, alarm) ? "（未見復歸紀錄）" : ""), alarm.Cause));
        foreach (var c in w.Commands.Values.Where(c => c.Vehicle == el.Id && !c.IsFinished))
            Detail.Add(Line("命令", CommandText(c), c.StatusCause));
    }

    string PalletText(PalletState p) =>
        p.Id + (p.Bins is { } bins ? $"（桶 {bins.Count} 個：{string.Join("、", bins)}）" : "（桶清單未知）") + (p.Preexisting ? "；案件開始前已存在" : "");

    string CommandText(CommandState c) =>
        $"{c.Id}（{c.Type ?? "類型未知"}）" + StatusText(c) + (_model?.IsUnfinishedInCase(c.Id) == true ? "；案件範圍內未完成" : "");

    /// <summary>命令狀態的文字（ADR-0005 第 6 點）；搬送機以 Layout 上的名稱顯示。</summary>
    public string StatusText(CommandState c) => c.Status switch
    {
        CommandStatus.Created => "已建立",
        CommandStatus.SegmentRunning => $"第 {c.Segment} 段執行中（{NameOf(c.Vehicle)} → {c.SegmentDestination}）",
        CommandStatus.SegmentArrived => $"第 {c.Segment} 段已抵達",
        CommandStatus.SegmentFinished => $"第 {c.Segment} 段作業完成（地點未記錄）",
        CommandStatus.Completed => "已完成",
        CommandStatus.Cancelled => "已取消",
        _ => "已完成（已交出）",
    };

    string? NameOf(string? elementId) => Elements.FirstOrDefault(e => e.Id == elementId)?.Name ?? elementId;

    // ---- 設備狀態表（ADR-0008 第 6 點） ----

    /// <summary>站點、搬送機、對接設備在目前時刻的狀態；與 Layout 面板使用同一份世界狀態。</summary>
    public ObservableCollection<EquipmentRowViewModel> Equipment { get; } = [];

    /// <summary>只看異常中或不一致的設備。</summary>
    [ObservableProperty]
    public partial bool OnlyAbnormal { get; set; }

    [ObservableProperty]
    public partial EquipmentSort SortBy { get; private set; } = EquipmentSort.Name;

    [ObservableProperty]
    public partial bool SortDescending { get; private set; }

    partial void OnOnlyAbnormalChanged(bool value) => UpdateEquipment();

    /// <summary>依欄位排序；再按同一欄位時反向。</summary>
    [RelayCommand]
    void SortEquipment(EquipmentSort column)
    {
        SortDescending = SortBy == column && !SortDescending;
        SortBy = column;
        UpdateEquipment();
    }

    void UpdateEquipment()
    {
        var rows = Elements.Where(e => e.Type is LayoutElementType.Station or LayoutElementType.Vehicle or LayoutElementType.Dock)
            .Select(e => new EquipmentRowViewModel(e.Id, e.Name, e.TypeLabel, e.Aliases, EquipmentState(e), _state.Alarms.ContainsKey(e.Id) || e.IsInconsistent, e.Look))
            .Where(r => !OnlyAbnormal || r.IsAbnormal);
        Func<EquipmentRowViewModel, string> key = SortBy switch
        {
            EquipmentSort.Type => r => r.TypeLabel + "\u0001" + r.Name,
            EquipmentSort.State => r => r.State,
            EquipmentSort.Abnormal => r => (r.IsAbnormal ? "0" : "1") + r.Name,
            _ => r => r.Name,
        };
        var sorted = SortDescending ? rows.OrderByDescending(key, StringComparer.CurrentCulture) : rows.OrderBy(key, StringComparer.CurrentCulture);
        Equipment.Clear();
        foreach (var r in sorted) Equipment.Add(r);
    }

    /// <summary>設備在目前時刻的狀態摘要；推不出來的部分顯示「未知」。</summary>
    string EquipmentState(PlaybackElementViewModel e)
    {
        var w = _state;
        var parts = new List<string>();
        if (w.Alarms.TryGetValue(e.Id, out var alarm))
            parts.Add($"異常中（警報 {alarm.Code}{(_model?.IsAlarmNeverReset(e.Id, alarm) == true ? "，未見復歸紀錄" : "")}）");
        switch (e.Type)
        {
            case LayoutElementType.Station:
                w.Stations.TryGetValue(e.Id, out var s);
                parts.Add("在席 " + (s?.Presence switch { true => "ON", false => "OFF", null => "未知" }));
                if (s?.Mode is { } mode) parts.Add("模式 " + StationModeText(mode)); // 模式未知時不顯示
                if (s?.Status is { } status) parts.Add("運轉 " + status); // 運轉狀態未知時不顯示
                var pallets = w.PalletsAt(LocationKind.Station, e.Id).Select(p => p.Id).ToList();
                if (pallets.Count > 0) parts.Add("貨物 " + string.Join("、", pallets));
                if (e.IsInconsistent) parts.Add("不一致");
                break;
            case LayoutElementType.Vehicle:
                w.Vehicles.TryGetValue(e.Id, out var v);
                parts.Add($"連線 {v?.Connection ?? "未知"}");
                parts.Add($"模式 {v?.Mode ?? "未知"}");
                parts.Add(v?.Work switch
                {
                    VehicleWork.Executing => $"執行中 {v.Command} → {v.Destination}",
                    VehicleWork.Idle => "閒置",
                    _ => "作業未知",
                });
                break;
            case LayoutElementType.Dock:
                var delivered = w.PalletsAt(LocationKind.Delivered, e.Id).Count();
                parts.Add(delivered > 0 ? $"已交出 {delivered}" : "未交出");
                break;
        }
        return string.Join("；", parts);
    }

    /// <summary>選取元素（Layout 面板上點一下）。</summary>
    [RelayCommand]
    void SelectElement(PlaybackElementViewModel? element) => SelectedElement = element;

    /// <summary>點料架的一列：顯示側視圖。</summary>
    public void SelectRackCell(RackCellViewModel? cell)
    {
        if (_selectedCell is not null) _selectedCell.IsSelected = false;
        _selectedCell = cell;
        if (cell is not null)
        {
            cell.IsSelected = true;
            SelectedElement = Elements.FirstOrDefault(e => e.Cells.Contains(cell));
        }
        UpdateSideView();
    }

    void UpdateSideView()
    {
        SideView.Clear();
        if (_selectedCell is not { } cell || Elements.FirstOrDefault(e => e.Cells.Contains(cell)) is not { } rack)
        {
            SideViewTitle = null;
            return;
        }
        SideViewTitle = $"{rack.Name}　第 {cell.Column} 列（由上而下）";
        for (int level = cell.Levels; level >= 1; level--)
        {
            var code = StorageCode.Format(_storageRule, rack.Model.Row ?? "", cell.Column, level);
            string? pallet = code is not null && _state.Storage.TryGetValue(code, out var p) ? p : null;
            long? cause = pallet is not null ? _state.Pallets[pallet].LocationCause : null;
            SideView.Add(new RackLevelViewModel(level, code, pallet, cause, Line("", "", cause).CauseText));
        }
    }

    /// <summary>跳到造成狀態的事件（主畫面選取它，目前時刻也跟著移動）。</summary>
    [RelayCommand]
    void GoToCause(long? sequence)
    {
        if (sequence is { } s) GoToEvent?.Invoke(s);
    }
}
