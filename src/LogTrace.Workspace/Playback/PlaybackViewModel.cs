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
    /// <summary>防火門開（LOG 確定或推估；推估時 IsEstimated）。</summary>
    DoorOpen,
    /// <summary>防火門關（LOG 確定或推估；推估時 IsEstimated）。</summary>
    DoorClosed,
    /// <summary>BCR 讀取中、磅秤等待中（ADR-0027）。</summary>
    Reading,
    /// <summary>BCR 讀到條碼、磅秤有讀值且沒有超出上下限（ADR-0027）。</summary>
    ReadOk,
    /// <summary>手動輸入條碼（ADR-0027）。</summary>
    Manual,
}

/// <summary>設備狀態表的排序欄位。</summary>
public enum EquipmentSort
{
    Name,
    Type,
    State,
    Abnormal,
}

/// <summary>設備狀態表的一列（ADR-0008 第 6 點）：站點、搬送機、對接設備、防火門在目前時刻的狀態。</summary>
/// <param name="IsAbnormal">異常中（有未復歸的警報）或不一致。</param>
/// <param name="Floor">所在的樓層（ADR-0006 第 13 點）。</param>
public sealed record EquipmentRowViewModel(string Id, string Name, string TypeLabel, string Aliases, string State, bool IsAbnormal, ElementLook Look, string Floor = "");

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

/// <summary>料架側視圖的一層。IsEmpty＝LOG 記載已取出；Pallet 與 IsEmpty 都沒有時為未取得資訊（ADR-0005 第 2 點 2026-11-04 補充）。</summary>
/// <param name="Depth">雙深料架的深淺（「淺」或「深」，ADR-0019 第 7 點）；單深為 null。</param>
public sealed record RackLevelViewModel(int Level, string? StorageCode, string? Pallet, bool IsEmpty, long? Cause, string? CauseText, string? Depth = null)
{
    /// <summary>棧板欄的顯示：棧板編號、「空」，或未取得資訊的「—」。</summary>
    public string Content => Pallet ?? (IsEmpty ? "空" : "—");
}

/// <summary>Layout 面板上的一個元素與它在目前時刻的狀態。</summary>
public sealed partial class PlaybackElementViewModel : ObservableObject
{
    /// <param name="floors">Layout 的樓層清單（ADR-0006 第 13 點）；null 時為只有 1F。</param>
    public PlaybackElementViewModel(LayoutElement e, IReadOnlyDictionary<string, LayoutElement> byId, IReadOnlyList<string>? floors = null)
    {
        Model = e;
        (X, Y, W, H, Rotation) = (e.X ?? 0, e.Y ?? 0, e.W ?? 0, e.H ?? 0, e.Rotation ?? 0);
        LayoutElement? Find(string? id) => id is not null && byId.TryGetValue(id, out var x) ? x : null;
        ServedFloors = LayoutFloors.FloorsOf(e, floors ?? [LayoutFloors.Default], Find);
        Floor = e.Type == LayoutElementType.Elevator ? ServedFloors.FirstOrDefault() ?? (floors ?? [LayoutFloors.Default])[0]
            : LayoutFloors.FloorOf(e, floors ?? [LayoutFloors.Default], Find);
        Deck = LayoutFloors.DeckOf(e, Find);
        if (e.Type == LayoutElementType.Conveyor && e.From is { } f && e.To is { } t && byId.TryGetValue(f, out var a) && byId.TryGetValue(t, out var b))
            ConveyorEnds = (End(a, e.FromPoint), End(b, e.ToPoint));
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
    public bool IsFireDoor => Type == LayoutElementType.FireDoor;
    public bool IsRail => Type == LayoutElementType.Rail;
    public bool HasGeometry => !IsConveyor && !LayoutElement.HasPath(Type);
    public bool IsAuxiliary => Type is LayoutElementType.Conveyor or LayoutElementType.Area or LayoutElementType.Label or LayoutElementType.Rail
        or LayoutElementType.Fence or LayoutElementType.FenceGate or LayoutElementType.Eq; // 圍欄、圍欄門、EQ 不推導狀態（ADR-0020）

    public double X { get; }
    public double Y { get; }
    public double W { get; }
    public double H { get; }
    public int Rotation { get; }

    /// <summary>所在的樓層（輸送線取起點的樓層；電梯取第一個服務的樓層）。</summary>
    public string Floor { get; }

    /// <summary>同一樓層中的層段（輸送線取起點的層段；電梯為下層）。</summary>
    public LayoutDeck Deck { get; }

    /// <summary>畫在哪些樓層：電梯為服務的樓層，其他元素只有 Floor（ADR-0006 第 15 點）。</summary>
    public IReadOnlyList<string> ServedFloors { get; }

    public bool IsElevator => Type == LayoutElementType.Elevator;
    public bool IsRm => Type == LayoutElementType.Rm;

    /// <summary>設備狀態表的「樓層」欄：電梯為目前樓層（未取得資訊時為「—」），其他元素為所在的樓層。</summary>
    [ObservableProperty]
    public partial string? CurrentFloorText { get; set; }

    /// <summary>輸送線兩端的位置（公尺）；兩端都找得到時才有，其他元素為 null。</summary>
    public ((double X, double Y) From, (double X, double Y) To)? ConveyorEnds { get; }

    /// <summary>
    /// 沿導軌移動的搬送機在目前時刻的位置與依據（ADR-0005 第 10 點）；其他元素為 null（畫在 Layout 上放的位置）。
    /// </summary>
    [ObservableProperty]
    public partial VehiclePlacement? Placement { get; set; }

    /// <summary>搭配導軌的 Conv 在目前時刻的位置與依據（ADR-0028）；其他元素為 null。</summary>
    [ObservableProperty]
    public partial ConvPlacement? ConvPlace { get; set; }

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

    /// <summary>命令與在席訊號矛盾（ADR-0005 第 3 點），或貨物在這一筆事件穿過關著的防火門。</summary>
    [ObservableProperty]
    public partial bool IsInconsistent { get; set; }

    /// <summary>不一致的說明（滑鼠停留時顯示）。</summary>
    [ObservableProperty]
    public partial string? InconsistentText { get; set; }

    /// <summary>EQ 的門開著（推估，ADR-0020 補充）：緊鄰的 Conv 上有棧板等待完成回報時；其他元素為 false。</summary>
    [ObservableProperty]
    public partial bool IsDoorOpen { get; set; }

    /// <summary>電梯：抵達後朝目的地的那一側閘門畫成開（推估，ADR-0020 第 3 點）；其他時間或其他元素為 null。</summary>
    [ObservableProperty]
    public partial string? OpenGate { get; set; }

    /// <summary>可能卡住（推估，ADR-0005 第 5 點 2026-11-16 補充，#142）：貨物在這個元素上卡住（命令沒有下一步或站點沒有派送）。</summary>
    [ObservableProperty]
    public partial bool IsStalled { get; set; }

    /// <summary>BCR 讀取中或磅秤等待中（ADR-0027）：3D 畫出掃描光束；其他元素為 false。</summary>
    [ObservableProperty]
    public partial bool IsReading { get; set; }

    /// <summary>BCR 讀取失敗、磅秤超出上下限（ADR-0027）：設備狀態表的「只看異常」也包含。</summary>
    [ObservableProperty]
    public partial bool IsDeviceAbnormal { get; set; }

    /// <summary>
    /// BCR、磅秤的結果超過 <see cref="PlaybackViewModel.StaleSeconds"/> 秒（#209）：標籤變淡；異常也一樣。
    /// 讀取中、等待中與選取中的元素不變淡。
    /// </summary>
    [ObservableProperty]
    public partial bool IsStale { get; set; }

    /// <summary>防火門的狀態是推估（還沒有 LOG 事件，用 Layout 的平時狀態）；畫面以虛線框表示。</summary>
    [ObservableProperty]
    public partial bool IsEstimated { get; set; }

    /// <summary>輸送線被關著的防火門阻斷（含推估）。</summary>
    [ObservableProperty]
    public partial bool IsBlocked { get; set; }

    /// <summary>輸送線的阻斷只來自推估的防火門狀態。</summary>
    [ObservableProperty]
    public partial bool IsBlockedEstimated { get; set; }

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
    VehicleMotion? _motion;
    ConvMotion? _convMotion;
    IReadOnlyList<Stall> _stalls = [];
    /// <summary>目前時刻的可能卡住：元素 id → 卡住的時段（含與沒有 LOG 重疊而不判定的）。</summary>
    Dictionary<string, List<Stall>> _stallsNow = new(StringComparer.Ordinal);

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

    /// <summary>3D 的場景（Layout 元素與樓層）；載入或清除案件時重建（ADR-0017 第 4 點）。沒有 Layout 時元素為空。</summary>
    [ObservableProperty]
    public partial SceneLayout Scene { get; private set; } = SceneLayout.Empty;

    /// <summary>3D 的目前時刻狀態：目前時刻或選取改變時更新；畫面把它傳給網頁。</summary>
    [ObservableProperty]
    public partial SceneState SceneNow { get; private set; } = SceneState.Empty;

    void RefreshSceneState()
    {
        if (_model is not null) SceneNow = PlaybackScene.StateOf(Elements, _state, SelectedElement, _selectedCell, _storageRule, CargoByPallet);
    }

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
        _stalls = [];
        _stallsNow.Clear();
        _state = new();
        Scene = SceneLayout.Empty;
        SceneNow = SceneState.Empty;
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
        IReadOnlyList<KeyValuePair<string, string>>? stationModeNames = null, IReadOnlyList<Stall>? stalls = null)
    {
        _model = model;
        _stalls = stalls ?? [];
        _stationModeNames = (stationModeNames ?? []).GroupBy(p => p.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
        _storageRule = layout.StorageCodeRule;
        _gates = layout.Elements.Where(e => e.Type == LayoutElementType.Elevator)
            .ToDictionary(e => e.Id, e => ElevatorGates.SidesOf(e, layout), StringComparer.Ordinal);
        _effectAt = model.Effects.GroupBy(x => x.Sequence).ToDictionary(g => g.Key, g => g.ToList());
        _events = events.ToDictionary(e => e.Sequence);
        _motion = new VehicleMotion(model.Effects, layout, seq => _events.TryGetValue(seq, out var ev) ? ev.Timestamp : null);
        _convMotion = new ConvMotion(model.PalletMoves, layout, seq => _events.TryGetValue(seq, out var ev) ? ev.Timestamp : null);
        var byId = layout.Elements.GroupBy(e => e.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        LoadFloors(layout);
        _rows = layout.Rows ?? [];
        Elements.Clear();
        foreach (var e in layout.Elements) Elements.Add(new PlaybackElementViewModel(e, byId, Floors));
        SelectedElement = null;
        Scene = PlaybackScene.LayoutOf(layout, Elements);
        Message = hasStateRules ? null : "廠別設定的解析規則中，只有在席等少數狀態變化；命令、搬送機、警報等需要在規則中設定「效果」才能推導。";
        SetMoment(0);
    }

    /// <summary>
    /// 目前時刻：事件序號 sequence（含）之前的所有事件套用後的狀態。time 是目前時刻的時間（播放時可能落在兩筆事件之間），
    /// 用來內插搬送機在導軌上的位置；null 時用該事件的時間。
    /// </summary>
    public void SetMoment(long sequence, DateTime? time = null)
    {
        if (_model is null) return;
        _state = _model.StateAt(sequence);
        _momentTime = time ?? (_events.TryGetValue(sequence, out var at) ? at.Timestamp : null);
        MomentText = sequence == 0 || !_events.TryGetValue(sequence, out var e)
            ? "案件開始（所有元素都是未知）"
            : $"{e.Timestamp:yyyy-MM-dd HH:mm:ss.ffff}　事件 #{sequence}";
        UpdateStallsNow();
        foreach (var el in Elements)
        {
            Update(el);
            el.IsStalled = _stallsNow.TryGetValue(el.Id, out var stalls) && stalls.Any(s => !s.OverlapsNoLog);
        }
        UpdateConveyorBlocks();
        UpdateDetail();
        UpdateSideView();
        UpdateEquipment();
        RefreshSceneState();
    }

    partial void OnSelectedElementChanged(PlaybackElementViewModel? oldValue, PlaybackElementViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        if (newValue is null || !newValue.IsRack) SelectRackCell(null);
        // 選取中的 BCR、磅秤不變淡（#209）：選取改變時重新計算這兩個元素
        foreach (var el in new[] { oldValue, newValue })
            if (el is { Type: LayoutElementType.Bcr or LayoutElementType.Scale }) Update(el);
        UpdateDetail();
        RefreshSceneState();
    }

    void Update(PlaybackElementViewModel el)
    {
        var w = _state;
        switch (el.Type)
        {
            case LayoutElementType.Conv or LayoutElementType.Workstation:
            {
                w.Stations.TryGetValue(el.Id, out var s);
                var pallets = w.PalletsAt(LocationKind.Station, el.Id).Select(p => p.Id).ToList();
                el.PresenceLight = s?.Presence switch { true => "●", false => "○", null => "◌" };
                el.StatusBadge = s?.Status is { Length: > 0 } status && status != NormalStationStatus ? status[..1] : null;
                el.IsInconsistent = w.IsInconsistent(el.Id);
                el.InconsistentText = el.IsInconsistent ? "不一致：命令與在席訊號矛盾" : null;
                el.Summary = pallets.Count > 0 ? string.Join("、", pallets) : null;
                el.Look = w.Alarms.ContainsKey(el.Id) ? ElementLook.Alarm
                    : pallets.Count > 0 ? ElementLook.Occupied
                    : s is null ? ElementLook.Unknown : ElementLook.Empty;
                // 搭配導軌的 Conv（ADR-0028）：位置依貨物推估；不在擺放的位置時標為推估
                if (el.Type == LayoutElementType.Conv && _convMotion?.PlacementAt(el.Id, w.Sequence, _momentTime) is { } cp)
                {
                    el.ConvPlace = cp;
                    el.Placement = new VehiclePlacement(cp.X, cp.Y, cp.IsEstimated ? VehiclePositionBasis.Estimated : VehiclePositionBasis.Unknown, cp.Cause);
                    el.IsEstimated = cp.IsEstimated;
                }
                break;
            }
            case LayoutElementType.Vehicle or LayoutElementType.Rm:
            {
                w.Vehicles.TryGetValue(el.Id, out var v);
                el.Placement = _motion?.PlacementAt(el.Id, w.Sequence, _momentTime);
                el.IsEstimated = el.Placement?.IsEstimated == true; // 推估的位置以虛線框表示
                var load = w.PalletsAt(LocationKind.Vehicle, el.Id).Select(p => p.Id).ToList();
                el.Summary = v?.Work == VehicleWork.Executing ? $"{v.Command} → {v.Destination}" : load.Count > 0 ? string.Join("、", load) : null;
                el.Look = w.Alarms.ContainsKey(el.Id) ? ElementLook.Alarm
                    : v?.Work == VehicleWork.Executing ? ElementLook.Executing
                    : v is null ? ElementLook.Unknown : ElementLook.Empty;
                break;
            }
            case LayoutElementType.Elevator:
            {
                // ADR-0005 第 11 點：每一層的圖示都顯示同樣的狀態與載著的棧板
                w.Elevators.TryGetValue(el.Id, out var e);
                var load = w.PalletsAt(LocationKind.Elevator, el.Id).Select(p => p.Id).ToList();
                el.CurrentFloorText = e?.Floor ?? "—";
                el.IsEstimated = e?.IsMoving == true || e?.FloorBasis == ElevatorFloorBasis.EstimatedFromSource;
                var where = e is null ? null : e.IsMoving ? $"移動中：{e.FromFloor ?? "?"} → {e.ToFloor ?? "?"}" : e.Floor is { } f ? $"在 {f}" : null;
                el.Summary = string.Join("　", new[] { where, load.Count > 0 ? string.Join("、", load) : null }.OfType<string>()) is { Length: > 0 } t ? t : null;
                el.Look = w.Alarms.ContainsKey(el.Id) ? ElementLook.Alarm
                    : e?.IsMoving == true ? ElementLook.Executing
                    : load.Count > 0 ? ElementLook.Occupied
                    : e is null ? ElementLook.Unknown : ElementLook.Empty;
                el.OpenGate = OpenGateOf(el, e);
                break;
            }
            case LayoutElementType.Eq:
            {
                // 貨物交給 EQ，收到完成回報才算已交出（ADR-0005 第 2 點、ADR-0026）
                var delivered = w.PalletsAt(LocationKind.Delivered, el.Id).Count();
                var waiting = w.PalletsAt(LocationKind.AtEq, el.Id).Count();
                el.Summary = DockSummary(waiting, delivered);
                el.Look = w.Alarms.ContainsKey(el.Id) ? ElementLook.Alarm
                    : waiting > 0 ? ElementLook.Occupied
                    : delivered > 0 ? ElementLook.Empty : ElementLook.Unknown;
                // 門的開關（推估）：有棧板等待完成回報時開，門朝緊鄰的 Conv（ADR-0020 補充）
                el.IsDoorOpen = waiting > 0;
                el.IsEstimated = el.IsDoorOpen;
                break;
            }
            case LayoutElementType.Bcr:
            {
                w.Bcrs.TryGetValue(el.Id, out var b);
                el.IsReading = b?.ReadingSince is not null;
                el.IsDeviceAbnormal = b?.IsFailed == true;
                el.Summary = BcrSummary(el, b);
                el.IsStale = !el.IsReading && !el.IsSelected && SecondsSince(b?.ResultCause) > StaleSeconds;
                el.Look = b is null ? ElementLook.Unknown
                    : el.IsReading ? ElementLook.Reading
                    : b.Result switch { BcrResult.Read => ElementLook.ReadOk, BcrResult.Manual => ElementLook.Manual, BcrResult.Failed => ElementLook.Alarm, _ => ElementLook.Unknown };
                break;
            }
            case LayoutElementType.Scale:
            {
                w.Scales.TryGetValue(el.Id, out var s);
                var judgement = w.Judge(s);
                el.IsReading = s?.WaitingSince is not null;
                el.IsEstimated = el.IsReading; // 等待中是推估（LOG 沒有記錄磅秤要求）
                el.IsDeviceAbnormal = judgement is WeightJudgement.Over or WeightJudgement.Under;
                el.Summary = ScaleSummary(s, judgement);
                el.IsStale = !el.IsReading && !el.IsSelected && SecondsSince(s?.WeightCause) > StaleSeconds;
                el.Look = s is null ? ElementLook.Unknown
                    : el.IsReading ? ElementLook.Reading
                    : el.IsDeviceAbnormal ? ElementLook.Alarm
                    : s.WeightText is not null ? ElementLook.ReadOk : ElementLook.Unknown;
                break;
            }
            case LayoutElementType.FireDoor:
            {
                var (state, _, estimated) = DoorStateOf(el);
                el.IsEstimated = estimated;
                el.Summary = state is null ? null : (estimated ? "推估：" : "") + DoorStates.Text(state);
                el.Look = state switch { DoorState.Open => ElementLook.DoorOpen, DoorState.Closed => ElementLook.DoorClosed, _ => ElementLook.Unknown };
                var crossing = CrossingNow(el.Id);
                el.IsInconsistent = crossing is not null;
                el.InconsistentText = crossing is null ? null : $"不一致：防火門關著，但棧板 {crossing.Pallet} 經過「{NameOf(crossing.Conveyor)}」移動";
                break;
            }
            case LayoutElementType.Rack:
            {
                int total = 0, empty = 0;
                foreach (var cell in el.Cells)
                {
                    var slots = Enumerable.Range(1, cell.Levels)
                        .Select(l => StorageCode.Format(_storageRule, el.Model.Row ?? "", cell.Column, l) is { } code && w.Storage.TryGetValue(code, out var slot) ? slot : null)
                        .Where(slot => slot is not null).ToList();
                    cell.Occupied = slots.Count(slot => !slot!.IsEmpty);
                    total += cell.Occupied;
                    empty += slots.Count - cell.Occupied;
                }
                el.Summary = (total, empty) switch
                {
                    (0, 0) => null,
                    (_, 0) => $"{total} 個儲位有貨",
                    (0, _) => $"{empty} 個儲位空",
                    _ => $"{total} 個儲位有貨、{empty} 個空",
                };
                el.Look = total > 0 ? ElementLook.Occupied : ElementLook.Unknown;
                break;
            }
        }
    }

    // ---- BCR 與磅秤（ADR-0027） ----

    /// <summary>結果超過這麼多秒時標籤變淡（#209，使用者決定 2 分鐘）。</summary>
    public const double StaleSeconds = 120;

    /// <summary>事件 sequence 到目前時刻經過的秒數；時間不明時為 null。</summary>
    double? SecondsSince(long? sequence) =>
        sequence is { } s && _momentTime is { } now && _events.TryGetValue(s, out var e) ? Math.Max(0, (now - e.Timestamp).TotalSeconds) : null;

    static string Ago(double? seconds) => seconds is { } s ? $"（{Math.Floor(s):0} 秒前）" : "";

    /// <summary>條碼的簡短顯示：超過 8 個字時只顯示末 6 碼（9a）。</summary>
    public static string ShortCode(string code) => code.Length > 8 ? "…" + code[^6..] : code;

    /// <summary>BCR 的標籤：讀取中 n 秒、讀到的簡短結果、手動、失敗與錯誤代碼；結果註明幾秒前（ADR-0027 第 3 點）。</summary>
    string? BcrSummary(PlaybackElementViewModel el, BcrState? b)
    {
        if (b is null) return null;
        if (b.ReadingSince is { } since) return SecondsSince(since) is { } s ? $"讀取中 {Math.Floor(s):0} 秒" : "讀取中";
        var ago = Ago(SecondsSince(b.ResultCause));
        var codes = el.Model.Reads == BcrReads.Bin ? $"桶 {b.Barcodes.Count} 個" : string.Join("、", b.Barcodes.Select(ShortCode));
        return b.Result switch
        {
            BcrResult.Read => codes + ago,
            BcrResult.Manual => $"手動 {codes}{ago}",
            BcrResult.Failed => $"讀取失敗{(b.ErrorCode is { } code ? " " + code : "")}{ago}",
            _ => null,
        };
    }

    /// <summary>磅秤的標籤：等待中（推估）、讀值（kg，假設）與上下限比對（ADR-0027 第 4 點）。</summary>
    string? ScaleSummary(ScaleState? s, WeightJudgement judgement)
    {
        if (s is null) return null;
        if (s.WaitingSince is { } since) return "等待中（推估）" + (SecondsSince(since) is { } sec ? $" {Math.Floor(sec):0} 秒" : "");
        if (s.WeightText is not { } text) return null;
        var value = s.Weight is { } kg ? $"{Num(kg)} kg" : text;
        return value + judgement switch
        {
            WeightJudgement.Over => " 超重",
            WeightJudgement.Under => " 過輕",
            WeightJudgement.NoLimits => " 無上下限",
            _ => "",
        } + Ago(SecondsSince(s.WeightCause));
    }

    static string Num(double? v) => v is { } d ? d.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "—";

    static string LimitsText(WeightLimitState? l) => l is null ? "無上下限（沒有資料）"
        : !l.HasLimits ? "無上下限（上下限為 0 或空白，不判定）"
        : $"下限 {Num(l.Min is > 0 ? l.Min : null)} kg～上限 {Num(l.Max is > 0 ? l.Max : null)} kg";

    Dictionary<string, string> _stationModeNames = [];
    Dictionary<string, IReadOnlyList<string>> _gates = new(StringComparer.Ordinal);
    Dictionary<long, List<Engine.Parsing.EventEffect>> _effectAt = [];

    /// <summary>
    /// 電梯朝目的地的閘門是否開著（推估，ADR-0020 第 3 點）：台車已抵達（目前樓層依 LOG）且還沒有下一次派送時，
    /// 抵達事件的目的地設備在哪一側，且那一側有閘門，就畫成開。
    /// </summary>
    string? OpenGateOf(PlaybackElementViewModel el, ElevatorState? e)
    {
        if (e is null || e.IsMoving || e.Work == VehicleWork.Executing || e.FloorBasis != ElevatorFloorBasis.Arrived || e.FloorCause is not { } seq) return null;
        if (_model is null || !_gates.TryGetValue(el.Id, out var gates) || gates.Count == 0) return null;
        var dest = _effectAt.GetValueOrDefault(seq)?.Select(x => x.Get(Engine.Parsing.FieldKinds.Destination)).OfType<string>().FirstOrDefault();
        if (_model.Resolve(dest) is not { } target || target.Id == el.Id) return null;
        var side = ElevatorGates.SideFacing(el.Model, (target.X ?? 0) + (target.W ?? 0) / 2, (target.Y ?? 0) + (target.H ?? 0) / 2);
        return gates.Contains(side) ? side : null;
    }
    IReadOnlyList<LayoutRow> _rows = [];
    DateTime? _momentTime;

    /// <summary>電梯目前樓層與依據的文字（ADR-0005 第 11 點）。</summary>
    public static string ElevatorFloorText(ElevatorState? e) => e?.Floor is not { } floor ? "未取得資訊"
        : e.IsMoving ? $"{floor}（移動中，推估）"
        : e.FloorBasis switch
        {
            ElevatorFloorBasis.Arrived => $"{floor}（依 LOG：抵達的 Conv 所在的樓層）",
            ElevatorFloorBasis.EstimatedFromSource => $"{floor}（推估：依派送的來源 Conv）",
            ElevatorFloorBasis.FinishedNoLocation => $"{floor}（作業完成，地點未記錄：停在出發的樓層）",
            ElevatorFloorBasis.Cancelled => $"{floor}（命令取消：停在出發的樓層）",
            _ => floor,
        };

    /// <summary>搭配導軌的 Conv 位置依據的文字（ADR-0028）。</summary>
    string ConvPlacementText(ConvPlacement p) =>
        p.Cause is null ? "Layout 擺放的位置（還沒有依貨物對齊過）"
        : p.Moving ? $"推估：移動中，前往對齊「{NameOf(p.Target)}」（兩次對齊之間依時間內插）"
        : $"推估：對齊「{NameOf(p.Target)}」（{(p.Entering ? "貨物從那裡進入" : "貨物往那裡離開")}；LOG 沒有記錄 Conv 的位置）";

    /// <summary>搬送機位置依據的文字（ADR-0005 第 10 點）。</summary>
    public static string PlacementText(VehiclePositionBasis basis) => basis switch
    {
        VehiclePositionBasis.Estimated => "推估（派送到抵達之間依時間比例內插，不是實際位置）",
        VehiclePositionBasis.Arrived => "依 LOG：已抵達（位置為目的地在導軌上的投影）",
        VehiclePositionBasis.WaitingArrival => "執行中，抵達時間未知（停在起點）",
        VehiclePositionBasis.FinishedNoLocation => "作業完成，地點未記錄（停在起點）",
        VehiclePositionBasis.Cancelled => "命令取消（停在起點）",
        _ => "位置未取得資訊（畫在 Layout 上放的位置）",
    };

    /// <summary>
    /// 防火門在目前時刻的狀態：LOG 事件確定的優先；沒有 LOG 事件時用 Layout 的平時狀態（推估）；都沒有時為 null（未取得資訊）。
    /// </summary>
    (DoorState? State, long? Cause, bool Estimated) DoorStateOf(PlaybackElementViewModel door) =>
        _state.Doors.TryGetValue(door.Id, out var d) && d.State is { } s ? (s, d.StateCause, false)
        : door.Model.DoorState is { } usual ? (usual, null, true)
        : (null, null, false);

    /// <summary>貨物在目前這一筆事件穿過關著的防火門（只在那一筆事件的時刻標示）。</summary>
    DoorCrossing? CrossingNow(string doorId) =>
        _state.Doors.TryGetValue(doorId, out var d) && d.LastCrossing is { } c && c.Cause == _state.Sequence && _state.Sequence > 0 ? c : null;

    /// <summary>輸送線的阻斷：關聯到關著的防火門（含推估）時畫成阻斷；只有推估的門時標為推估。</summary>
    void UpdateConveyorBlocks()
    {
        var blocked = new Dictionary<string, bool>(StringComparer.Ordinal); // 輸送線 id → 是否只有推估
        foreach (var door in Elements.Where(e => e.IsFireDoor))
        {
            var (state, _, estimated) = DoorStateOf(door);
            if (state != DoorState.Closed) continue;
            foreach (var c in door.Model.Conveyors ?? [])
                blocked[c] = blocked.TryGetValue(c, out var onlyEstimated) ? onlyEstimated && estimated : estimated;
        }
        foreach (var c in Elements.Where(e => e.IsConveyor))
        {
            c.IsBlocked = blocked.TryGetValue(c.Id, out var onlyEstimated);
            c.IsBlockedEstimated = c.IsBlocked && onlyEstimated;
        }
    }

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
        if (Floors.Count > 1 && !el.IsElevator) Detail.Add(Line("樓層", el.Floor, null));
        switch (el.Type)
        {
            case LayoutElementType.Conv or LayoutElementType.Workstation:
            {
                w.Stations.TryGetValue(el.Id, out var s);
                Detail.Add(Line("在席", s?.Presence switch { true => "ON", false => "OFF", null => "未知" }, s?.PresenceCause));
                Detail.Add(Line("Conv 模式", StationModeText(s?.Mode), s?.ModeCause));
                Detail.Add(Line("運轉狀態", s?.Status ?? "未知", s?.StatusCause));
                foreach (var (name, (on, cause)) in s?.Signals ?? []) Detail.Add(Line(name, on ? "ON" : "OFF", cause));
                foreach (var p in w.PalletsAt(LocationKind.Station, el.Id))
                    Detail.Add(Line("貨物", PalletText(p), p.LocationCause));
                if (w.IsInconsistent(el.Id))
                    Detail.Add(Line("不一致", "命令說貨物在這個 Conv，但在席是 OFF（不判斷哪一邊才對）", s?.PresenceCause));
                if (el.ConvPlace is { } cp)
                    Detail.Add(Line("位置", ConvPlacementText(cp), cp.Cause));
                break;
            }
            case LayoutElementType.Vehicle or LayoutElementType.Rm:
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
                if (el.Placement is { } pl)
                {
                    Detail.Add(Line("位置", PlacementText(pl.Basis), pl.ArrivalCause ?? pl.DispatchCause));
                    if (pl.DispatchCause is { } d && pl.ArrivalCause is not null) Detail.Add(Line("派送", "這一段的派送", d));
                    if (pl.Lift is { } lift) Detail.Add(Line("載貨台", $"約 {lift:0.0} 公尺（推估：依儲位的層）", pl.ArrivalCause ?? pl.DispatchCause));
                }
                break;
            }
            case LayoutElementType.Elevator:
            {
                w.Elevators.TryGetValue(el.Id, out var e);
                Detail.Add(Line("服務的樓層", string.Join("、", el.ServedFloors), null));
                Detail.Add(Line("目前樓層", ElevatorFloorText(e), e?.FloorCause));
                Detail.Add(Line("作業", e?.Work switch
                {
                    VehicleWork.Executing => $"執行中：命令 {e.Command} → {e.Destination}（移動中：{e.FromFloor ?? "?"} → {e.ToFloor ?? "?"}，推估）",
                    VehicleWork.Idle => "閒置",
                    _ => "未知",
                }, e?.WorkCause));
                foreach (var p in w.PalletsAt(LocationKind.Elevator, el.Id))
                    Detail.Add(Line("載著", PalletText(p), p.LocationCause));
                if (_gates.GetValueOrDefault(el.Id) is { Count: > 0 } gates)
                    Detail.Add(Line("閘門", el.OpenGate is null
                        ? $"{gates.Count} 處，關著（推估：LOG 沒有記錄閘門）"
                        : $"{gates.Count} 處；朝目的地的那一處開著（推估：抵達後依目的地方向，LOG 沒有記錄閘門）", e?.FloorCause));
                break;
            }
            case LayoutElementType.Eq:
            {
                Detail.Add(Line("EQ", "其他公司的設備；貨物在這裡交出", null));
                int before = Detail.Count;
                foreach (var p in w.PalletsAt(LocationKind.AtEq, el.Id))
                    Detail.Add(Line("等待完成回報", PalletText(p), p.LocationCause));
                foreach (var p in w.PalletsAt(LocationKind.Delivered, el.Id))
                    Detail.Add(Line("已交出", PalletText(p), p.LocationCause));
                if (Detail.Count == before) Detail.Add(Line("已交出", "無", null));
                Detail.Add(Line("門", el.IsDoorOpen ? "開著（推估：有棧板等待完成回報；LOG 沒有記錄門）" : "關著（推估：沒有等待完成回報的棧板）", null));
                break;
            }
            case LayoutElementType.Bcr:
            {
                w.Bcrs.TryGetValue(el.Id, out var b);
                Detail.Add(Line("所屬 Conv", el.Model.Owner is { } owner ? NameOf(owner) ?? owner : "未指定", null));
                Detail.Add(Line("讀取對象", el.Model.Reads == BcrReads.Bin ? "桶" : "棧板", null));
                Detail.Add(Line("讀取", b?.ReadingSince is { } since ? $"讀取中（{Math.Floor(SecondsSince(since) ?? 0):0} 秒）" : b is null ? "未取得資訊" : "閒置", b?.ReadingSince));
                if (b?.Result is { } result)
                {
                    Detail.Add(Line("最近一次", result switch
                    {
                        BcrResult.Read => "讀到條碼",
                        BcrResult.Manual => "手動輸入",
                        _ => "讀取失敗" + (b.ErrorCode is { } code ? $"（{code}）" : "") + "：異常中，直到下一次讀到或手動輸入",
                    } + Ago(SecondsSince(b.ResultCause)), b.ResultCause));
                    foreach (var code in b.Barcodes) Detail.Add(Line("條碼", code, b.ResultCause));
                    if (b.BinsPallet is { } pallet) Detail.Add(Line("加入棧板", $"{pallet}（所屬 Conv 上只有這個棧板）", b.ResultCause));
                    else if (el.Model.Reads == BcrReads.Bin && result == BcrResult.Read)
                        Detail.Add(Line("加入棧板", "沒有加入（所屬 Conv 上沒有或不只一個棧板）", b.ResultCause));
                }
                break;
            }
            case LayoutElementType.Scale:
            {
                w.Scales.TryGetValue(el.Id, out var s);
                Detail.Add(Line("所屬 Conv", el.Model.Owner is { } owner ? NameOf(owner) ?? owner : "未指定", null));
                if (s?.WaitingSince is { } since)
                    Detail.Add(Line("等待中", "推估：所屬 Conv 開始讀取棧板 BCR，還沒有收到磅秤值（LOG 沒有記錄磅秤要求）", since));
                Detail.Add(Line("重量", s?.WeightText is null ? "未取得資訊"
                    : (s.Weight is { } kg ? $"{Num(kg)} kg（單位為假設）" : $"{s.WeightText}（不是數字）") + Ago(SecondsSince(s.WeightCause)), s?.WeightCause));
                if (s?.WeightText is not null)
                {
                    Detail.Add(Line("棧板", s.Pallet ?? "未取得資訊（所屬 Conv 上沒有或不只一個棧板）", s.WeightCause));
                    var limits = w.LimitsOf(s);
                    Detail.Add(Line("上下限", LimitsText(limits), limits?.Cause));
                    Detail.Add(Line("判定", w.Judge(s) switch
                    {
                        WeightJudgement.Over => "超重（異常）",
                        WeightJudgement.Under => "過輕（異常）",
                        WeightJudgement.Within => "在上下限內",
                        _ => "不判定（沒有上下限）",
                    }, limits?.Cause));
                }
                break;
            }
            case LayoutElementType.FireDoor:
            {
                var (state, cause, estimated) = DoorStateOf(el);
                Detail.Add(Line("狀態", state is null ? "未取得資訊"
                    : estimated ? $"{DoorStates.Text(state)}（推估：Layout 的平時狀態，還沒有 LOG 事件）"
                    : DoorStates.Text(state), cause));
                var conveyors = (el.Model.Conveyors ?? []).Select(NameOf).ToList();
                Detail.Add(Line("關聯的輸送線", conveyors.Count > 0 ? string.Join("、", conveyors) : "無", null));
                if (CrossingNow(el.Id) is { } crossing)
                    Detail.Add(Line("不一致", $"防火門關著，但棧板 {crossing.Pallet} 經過「{NameOf(crossing.Conveyor)}」移動（不判斷哪一邊才對）", crossing.Cause));
                break;
            }
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
        foreach (var s in _stallsNow.GetValueOrDefault(el.Id) ?? [])
            Detail.Add(s.OverlapsNoLog
                ? Line("超過門檻", $"{s.Reason}；這段時間有模組沒有 LOG，不判定為可能卡住（可能只是 LOG 缺漏）", s.LastSequence)
                : Line("可能卡住", $"推估：{s.Reason}（LOG 沒有記錄卡住，不推測原因）", s.LastSequence));
    }

    /// <summary>目前時刻的可能卡住，依貨物所在的元素整理（#142）。</summary>
    void UpdateStallsNow()
    {
        _stallsNow = new(StringComparer.Ordinal);
        if (_momentTime is not { } now) return;
        foreach (var s in _stalls.Where(s => s.IsActiveAt(now)))
            if (StallElement(s) is { } id)
            {
                if (!_stallsNow.TryGetValue(id, out var list)) _stallsNow[id] = list = [];
                list.Add(s);
            }
    }

    /// <summary>
    /// 卡住的標記放在哪個元素（Jev 0.88）：棧板當時所在的元素（站點、搬送機、電梯、對接設備；在儲位時為該儲位的料架）；
    /// 沒有棧板或位置不明時，放在執行這個命令的搬送機。
    /// </summary>
    string? StallElement(Stall s)
    {
        var w = _state;
        if (s.Pallet is { } pid && w.Pallets.TryGetValue(pid, out var p))
        {
            if (p.Location is LocationKind.Station or LocationKind.Vehicle or LocationKind.Elevator or LocationKind.AtEq or LocationKind.Delivered)
                return p.LocationId;
            if (p.Location == LocationKind.Storage && p.LocationId is { } code && StorageCode.TryParse(_storageRule, code, out var at))
                return Elements.FirstOrDefault(e => e.IsRack && e.Model.Row == at.Row && e.Model.ColFrom <= at.Column && at.Column <= e.Model.ColTo)?.Id;
        }
        return s.Command is { } cmd && w.Commands.TryGetValue(cmd, out var c) ? c.Vehicle : null;
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

    /// <summary>站點、搬送機、對接設備、防火門在目前時刻的狀態；與 Layout 面板使用同一份世界狀態。</summary>
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
        var rows = Elements.Where(e => e.Type is LayoutElementType.Conv or LayoutElementType.Workstation or LayoutElementType.Vehicle or LayoutElementType.Rm or LayoutElementType.Eq or LayoutElementType.FireDoor or LayoutElementType.Elevator
                or LayoutElementType.Bcr or LayoutElementType.Scale)
            .Select(e => new EquipmentRowViewModel(e.Id, e.Name, e.TypeLabel, e.Aliases, EquipmentState(e), _state.Alarms.ContainsKey(e.Id) || e.IsInconsistent || e.IsStalled || e.IsDeviceAbnormal, e.Look,
                e.IsElevator ? e.CurrentFloorText ?? "—" : e.Floor))
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
        if (e.IsStalled) parts.Add("可能卡住（推估）");
        switch (e.Type)
        {
            case LayoutElementType.Conv or LayoutElementType.Workstation:
                w.Stations.TryGetValue(e.Id, out var s);
                parts.Add("在席 " + (s?.Presence switch { true => "ON", false => "OFF", null => "未知" }));
                if (s?.Mode is { } mode) parts.Add("模式 " + StationModeText(mode)); // 模式未知時不顯示
                if (s?.Status is { } status) parts.Add("運轉 " + status); // 運轉狀態未知時不顯示
                var pallets = w.PalletsAt(LocationKind.Station, e.Id).Select(p => p.Id).ToList();
                if (pallets.Count > 0) parts.Add("貨物 " + string.Join("、", pallets));
                if (e.IsInconsistent) parts.Add("不一致");
                break;
            case LayoutElementType.Vehicle or LayoutElementType.Rm:
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
            case LayoutElementType.Eq:
                var delivered = w.PalletsAt(LocationKind.Delivered, e.Id).Count();
                var waiting = w.PalletsAt(LocationKind.AtEq, e.Id).Count();
                parts.Add(DockSummary(waiting, delivered) ?? "未交出");
                break;
            case LayoutElementType.Elevator:
                w.Elevators.TryGetValue(e.Id, out var ev);
                parts.Add("目前樓層 " + ElevatorFloorText(ev));
                parts.Add(ev?.IsMoving == true ? $"執行中 {ev.Command}（{ev.FromFloor ?? "?"} → {ev.ToFloor ?? "?"}）" : ev is null ? "作業未知" : "閒置");
                var load = w.PalletsAt(LocationKind.Elevator, e.Id).Select(p => p.Id).ToList();
                if (load.Count > 0) parts.Add("載著 " + string.Join("、", load));
                break;
            case LayoutElementType.Bcr or LayoutElementType.Scale:
                parts.Add(e.Summary ?? "未取得資訊");
                break;
            case LayoutElementType.FireDoor:
                var (state, _, estimated) = DoorStateOf(e);
                parts.Add(state is null ? "未取得資訊" : (estimated ? "推估 " : "") + DoorStates.Text(state));
                if (e.IsInconsistent) parts.Add("不一致");
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
        RefreshSceneState();
    }

    void UpdateSideView()
    {
        SideView.Clear();
        if (_selectedCell is not { } cell || Elements.FirstOrDefault(e => e.Cells.Contains(cell)) is not { } rack)
        {
            SideViewTitle = null;
            return;
        }
        var row = rack.Model.Row ?? "";
        // 雙深（排的配置表有深淺與配對，ADR-0019 第 7 點）：每層分淺、深兩格
        var config = _rows.FirstOrDefault(r => r.Row.Trim() == row.Trim());
        (string Row, string? Depth)[] pairs = config is { Depth: { } depth, Pair: { } pair }
            ? depth == RowDepth.Shallow ? [(row, "淺"), (pair, "深")] : [(pair, "淺"), (row, "深")]
            : [(row, null)];
        SideViewTitle = $"{rack.Name}　第 {cell.Column} 列（由上而下）" + (pairs.Length > 1 ? $"；雙深：淺＝第 {pairs[0].Row} 排、深＝第 {pairs[1].Row} 排" : "");
        for (int level = cell.Levels; level >= 1; level--)
            foreach (var (r, d) in pairs)
            {
                var code = StorageCode.Format(_storageRule, r, cell.Column, level);
                var slot = code is not null && _state.Storage.TryGetValue(code, out var sl) ? sl : null;
                long? cause = slot?.Cause;
                SideView.Add(new RackLevelViewModel(level, code, slot?.Pallet, slot?.IsEmpty == true, cause, Line("", "", cause).CauseText, d));
            }
    }

    /// <summary>跳到造成狀態的事件（主畫面選取它，目前時刻也跟著移動）。</summary>
    [RelayCommand]
    void GoToCause(long? sequence)
    {
        if (sequence is { } s) GoToEvent?.Invoke(s);
    }

    /// <summary>對接設備的摘要：抵達但還沒有完成回報的棧板，與已交出的棧板（ADR-0005 第 2 點）。</summary>
    static string? DockSummary(int waiting, int delivered) =>
        string.Join("、", new[] { waiting > 0 ? $"等待完成回報 {waiting}" : null, delivered > 0 ? $"已交出 {delivered}" : null }.OfType<string>()) is { Length: > 0 } t ? t : null;
}
