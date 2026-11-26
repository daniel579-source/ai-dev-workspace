using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using LogTrace.Engine.Playback;
using LogTrace.Engine.Plants;

namespace LogTrace.Workspace.Playback;

// 3D 回放的場景資料（ADR-0017 第 4 點）：C# 把 Layout 元素與目前時刻的狀態以 JSON 傳給網頁，
// 網頁（three.js）只負責畫出來與回報點選，不推導狀態。座標單位為公尺：x、y 為 Layout 平面（y 向下），z 為高度。

/// <summary>一個樓層：名稱、地板高度（下面各層樓板高度的和）、樓板高度與上層高度（上層地板離樓層地板的距離）。</summary>
public sealed record SceneFloor(string Name, double Elevation, double Height, double Upper = 0);

/// <summary>
/// 場景中的一個元素（靜態部分）。X、Y、W、H、Rotation 與 Layout 相同（左上角、寬、深、順時針角度）；Height 為元素高度，
/// 沒有填時依種類使用預設值（<see cref="PlaybackScene.DefaultHeight"/>）。Path 為導軌的折線或輸送線的兩端（公尺）。
/// Deck 為 "upper" 時元素在上層，Base 為上層地板離樓層地板的高度（公尺）；下層兩者皆為 null。
/// ToBase：輸送線兩端在不同層段時，終點那一端的層段高度（公尺；畫成斜線）；兩端同一層段時為 null。
/// Aim：BCR 掃描光束對準的位置（所屬 Conv 的中心與高度，[x, y, 高度]，ADR-0027）；沒有所屬 Conv 時為 null。
/// </summary>
public sealed record SceneElement(
    string Id, string Type, string Name, string Floor,
    double X, double Y, double W, double H, int Rotation, double Height,
    int? Levels = null, double? LevelHeight = null, int? ColFrom = null, int? ColTo = null,
    IReadOnlyList<double[]>? Path = null, bool Closed = false, bool Bidirectional = false,
    IReadOnlyList<string>? ServedFloors = null, IReadOnlyList<string>? Gates = null, string? Door = null,
    string? Deck = null, double? Base = null, double? ToBase = null, double[]? Aim = null);

/// <summary>場景的靜態部分；載入案件時傳一次。</summary>
public sealed record SceneLayout(IReadOnlyList<SceneFloor> Floors, IReadOnlyList<SceneElement> Elements)
{
    public static SceneLayout Empty { get; } = new([new SceneFloor(LayoutFloors.Default, 0, LayoutFloors.DefaultHeight)], []);
}

/// <summary>
/// 元素在目前時刻的狀態。Look 為圖例的顏色（<see cref="ElementLook"/>）。
/// X、Y：沿導軌移動的搬送機的位置（中心，公尺；推估時 Estimated）；其他元素為 null。
/// ElevatorFloor：電梯目前的樓層；移動中時為 null，改用 MovingFrom、MovingTo（推估）。
/// RackSlots：料架有貨的儲位（[列, 層]）。Lift：RM 載貨台的高度（公尺，推估）。
/// CargoLabel：搬送中的貨物（在搬送機、RM、電梯上）的標示，命令編號或棧板編號（#125）；沒有貨物時為 null。
/// Reading：BCR 讀取中、磅秤等待中（ADR-0027），畫出掃描光束；Summary 為標籤的簡短結果。Stale：結果超過 2 分鐘，標籤變淡（#209）。
/// </summary>
public sealed record SceneElementState(
    string Id, string Look, bool Selected = false,
    string? Presence = null, string? Badge = null, bool Inconsistent = false, bool Estimated = false,
    bool Blocked = false, bool BlockedEstimated = false, bool Loaded = false, bool Executing = false,
    string? Summary = null, double? X = null, double? Y = null,
    string? ElevatorFloor = null, string? MovingFrom = null, string? MovingTo = null,
    IReadOnlyList<int[]>? RackSlots = null, int? SelectedColumn = null, double? Lift = null, string? CargoLabel = null,
    bool Stalled = false, string? OpenGate = null, bool DoorOpen = false, bool Reading = false, bool Stale = false);

/// <summary>場景的目前時刻狀態；目前時刻或選取改變時傳。</summary>
public sealed record SceneState(long Sequence, IReadOnlyList<SceneElementState> Elements)
{
    public static SceneState Empty { get; } = new(0, []);
}

/// <summary>產生 3D 的場景資料。</summary>
public static class PlaybackScene
{
    /// <summary>
    /// 元素沒有填高度時的預設高度（公尺）。外觀以簡化的幾何表現（#113 使用者確認）：
    /// Conv 為低平台、搬送機為低矮的軌道車、EQ 中等高度、防火門為門板；料架依層數 × 每層高度。
    /// </summary>
    public static double DefaultHeight(LayoutElementType type) => type switch
    {
        // 除了料架、電梯、RM 之外的設備預設 0.8 公尺（ADR-0020 第 2 點）
        LayoutElementType.Conv or LayoutElementType.Workstation => 0.8,
        LayoutElementType.Vehicle => 0.8,
        LayoutElementType.Rm => 10, // 立柱的高度；載貨台依儲位的層移動（ADR-0019）
        LayoutElementType.Fence or LayoutElementType.FenceGate => 2.5, // 黃色網狀圍欄（ADR-0020）
        LayoutElementType.Eq => 1.8, // EQ（ADR-0020 補充）
        LayoutElementType.FireDoor => 3,
        LayoutElementType.Conveyor => 0.8,
        LayoutElementType.Rack => 1,
        LayoutElementType.Elevator => 1.2, // 台車的高度；井道依服務的樓層
        LayoutElementType.Label => 2,
        LayoutElementType.Bcr => 1.2, // 立柱上的讀取頭（ADR-0027）
        LayoutElementType.Scale => 0.1, // 貼在地板上的秤台（ADR-0027）
        _ => 0.02, // 區域、導軌：貼在地板上
    };

    /// <summary>料架沒有填每層高度時的預設（公尺）。</summary>
    public const double DefaultLevelHeight = 1.2;

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>傳給網頁的 JSON 訊息：{ "kind": "scene" | "state" | "focus", "data": … }。</summary>
    public static string Message(string kind, object data) =>
        JsonSerializer.Serialize(new { kind, data }, Json);

    /// <summary>以傳給網頁的格式寫出 JSON（用來比較場景是否改變）。</summary>
    public static string ToJson(object data) => JsonSerializer.Serialize(data, Json);

    /// <summary>只有 Layout（沒有回放狀態）時的場景，例如 Layout 編輯器（ADR-0018）。</summary>
    public static SceneLayout LayoutOf(LayoutSection layout)
    {
        var byId = layout.Elements.GroupBy(e => e.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var floors = LayoutFloors.Of(layout);
        return LayoutOf(layout, layout.Elements.Select(e => new PlaybackElementViewModel(e, byId, floors)).ToList());
    }

    public static SceneLayout LayoutOf(LayoutSection layout, IEnumerable<PlaybackElementViewModel> elements)
    {
        var floors = LayoutFloors.Stack(layout).Select(f => new SceneFloor(f.Floor, f.Elevation, f.Height, LayoutFloors.UpperHeightOf(layout, f.Floor))).ToList();
        var list = new List<SceneElement>();
        foreach (var e in elements)
        {
            int before = list.Count;
            AddElement(layout, e, list);
            if (e.Deck == LayoutDeck.Upper && list.Count > before)
                list[^1] = list[^1] with { Deck = "upper", Base = LayoutFloors.DeckBase(layout, e.Floor, LayoutDeck.Upper) };
        }
        return new SceneLayout(floors, list);
    }

    static void AddElement(LayoutSection layout, PlaybackElementViewModel e, List<SceneElement> list)
    {
        {
            var m = e.Model;
            var type = JsonNamingPolicy.CamelCase.ConvertName(m.Type.ToString());
            switch (m.Type)
            {
                case LayoutElementType.Rail or LayoutElementType.Fence:
                {
                    var path = RailPath.Of(m).Polyline().Select(p => new[] { p.X, p.Y }).ToList();
                    var height = m.Type == LayoutElementType.Fence && m.Height is > 0 ? m.Height.Value : DefaultHeight(m.Type);
                    list.Add(new SceneElement(e.Id, type, e.Name, e.Floor, 0, 0, 0, 0, 0, height, Path: path, Closed: m.Closed == true));
                    break;
                }
                case LayoutElementType.Conveyor:
                {
                    if (e.ConveyorEnds is not { } ends) break; // 兩端找不到時不畫（存檔時已檢查）
                    // 兩端在不同層段（上層與下層）：畫成從起點高度到終點高度的斜線
                    var toEnd = layout.Elements.FirstOrDefault(x => x.Id == m.To);
                    var toDeck = toEnd is null ? e.Deck : LayoutFloors.DeckOf(toEnd, _ => null);
                    double? toBase = toDeck != e.Deck ? LayoutFloors.DeckBase(layout, e.Floor, toDeck) : null;
                    list.Add(new SceneElement(e.Id, type, e.Name, e.Floor, 0, 0, 0, 0, 0, m.Height ?? DefaultHeight(m.Type),
                        Path: [[ends.From.X, ends.From.Y], [ends.To.X, ends.To.Y]], Bidirectional: m.Bidirectional == true, ToBase: toBase));
                    break;
                }
                case LayoutElementType.Rack:
                {
                    int levels = Math.Max(1, m.Levels ?? 1);
                    double levelHeight = m.LevelHeight is > 0 ? m.LevelHeight.Value : DefaultLevelHeight;
                    list.Add(new SceneElement(e.Id, type, e.Name, e.Floor, e.X, e.Y, e.W, e.H, e.Rotation, levels * levelHeight,
                        Levels: levels, LevelHeight: levelHeight, ColFrom: m.ColFrom, ColTo: m.ColTo));
                    break;
                }
                default:
                    list.Add(new SceneElement(e.Id, type, e.Name, e.Floor, e.X, e.Y, e.W, e.H, e.Rotation, m.Height is > 0 ? m.Height.Value : DefaultHeight(m.Type),
                        ServedFloors: e.IsElevator ? e.ServedFloors : null, Gates: e.IsElevator ? ElevatorGates.SidesOf(m, layout) : null,
                        Door: m.Type == LayoutElementType.Eq ? EqDoors.Of(m, layout).Side : null,
                        Aim: m.Type == LayoutElementType.Bcr ? AimOf(m, layout) : null));
                    break;
            }
        }
    }

    /// <summary>BCR 掃描光束對準所屬 Conv 的台面中心（ADR-0027）；所屬 Conv 不存在時為 null。</summary>
    static double[]? AimOf(LayoutElement bcr, LayoutSection layout) =>
        layout.Elements.FirstOrDefault(x => x.Id == bcr.Owner && LayoutElement.IsStationLike(x.Type)) is { } conv
            ? [(conv.X ?? 0) + (conv.W ?? 0) / 2, (conv.Y ?? 0) + (conv.H ?? 0) / 2, conv.Height is > 0 ? conv.Height.Value : DefaultHeight(conv.Type)]
            : null;

    /// <summary>目前時刻的狀態：沿用 Layout 回放面板已推導的元素狀態（<see cref="PlaybackViewModel"/>），不另外推導。</summary>
    public static SceneState StateOf(IEnumerable<PlaybackElementViewModel> elements, WorldState world, PlaybackElementViewModel? selected, RackCellViewModel? selectedCell, string? storageRule, bool cargoByPallet = false)
    {
        var list = new List<SceneElementState>();
        foreach (var e in elements)
        {
            var look = JsonNamingPolicy.CamelCase.ConvertName(e.Look.ToString());
            bool isSelected = ReferenceEquals(e, selected);
            switch (e.Type)
            {
                case LayoutElementType.Vehicle or LayoutElementType.Rm:
                {
                    world.Vehicles.TryGetValue(e.Id, out var v);
                    list.Add(new SceneElementState(e.Id, look, isSelected, Inconsistent: e.IsInconsistent, Estimated: e.IsEstimated,
                        Loaded: world.PalletsAt(LocationKind.Vehicle, e.Id).Any(), Executing: v?.Work == VehicleWork.Executing,
                        Summary: e.Summary, X: e.Placement?.X, Y: e.Placement?.Y, Lift: e.Placement?.Lift,
                        CargoLabel: CargoLabel(world, LocationKind.Vehicle, e.Id, cargoByPallet)));
                    break;
                }
                case LayoutElementType.Elevator:
                {
                    world.Elevators.TryGetValue(e.Id, out var ev);
                    bool moving = ev?.IsMoving == true;
                    list.Add(new SceneElementState(e.Id, look, isSelected, Estimated: e.IsEstimated,
                        Loaded: world.PalletsAt(LocationKind.Elevator, e.Id).Any(), Executing: moving, Summary: e.Summary,
                        ElevatorFloor: moving ? null : ev?.Floor, MovingFrom: moving ? ev!.FromFloor : null, MovingTo: moving ? ev!.ToFloor : null,
                        CargoLabel: CargoLabel(world, LocationKind.Elevator, e.Id, cargoByPallet), OpenGate: e.OpenGate));
                    break;
                }
                case LayoutElementType.Rack:
                {
                    var slots = new List<int[]>();
                    foreach (var cell in e.Cells)
                        for (int level = 1; level <= cell.Levels; level++)
                            if (RackSlotOccupied(world, storageRule, e, cell.Column, level)) slots.Add([cell.Column, level]);
                    list.Add(new SceneElementState(e.Id, look, isSelected, Summary: e.Summary, RackSlots: slots,
                        SelectedColumn: selectedCell is not null && e.Cells.Contains(selectedCell) ? selectedCell.Column : null));
                    break;
                }
                case LayoutElementType.Conv or LayoutElementType.Workstation:
                    // 搭配導軌的 Conv：X、Y 為推估的位置（ADR-0028）
                    list.Add(new SceneElementState(e.Id, look, isSelected, Presence: e.PresenceLight switch { "●" => "on", "○" => "off", _ => "unknown" },
                        Badge: e.StatusBadge, Inconsistent: e.IsInconsistent, Loaded: e.Look == ElementLook.Occupied || world.PalletsAt(LocationKind.Station, e.Id).Any(),
                        Summary: e.Summary, Estimated: e.ConvPlace?.IsEstimated == true, X: e.ConvPlace?.X, Y: e.ConvPlace?.Y));
                    break;
                default:
                    list.Add(new SceneElementState(e.Id, look, isSelected, Inconsistent: e.IsInconsistent, Estimated: e.IsEstimated,
                        Blocked: e.IsBlocked, BlockedEstimated: e.IsBlockedEstimated, Summary: e.Summary, DoorOpen: e.IsDoorOpen, Reading: e.IsReading, Stale: e.IsStale));
                    break;
            }
        }
        // 可能卡住（推估，#142）：所有種類的元素都可能帶這個標記
        var stalled = elements.Where(e => e.IsStalled).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        return new SceneState(world.Sequence, stalled.Count == 0 ? list : list.Select(s => stalled.Contains(s.Id) ? s with { Stalled = true } : s).ToList());
    }

    /// <summary>
    /// 搬送中貨物的標示（#125）：設備上所有棧板，以「、」分隔；命令編號模式下取棧板所屬的命令（優先未完成的、最近的），
    /// 找不到命令時顯示「—」（未取得資訊）。沒有貨物時為 null。
    /// </summary>
    public static string? CargoLabel(WorldState world, LocationKind kind, string id, bool byPallet)
    {
        var pallets = world.PalletsAt(kind, id).Select(p => p.Id).Order(StringComparer.Ordinal).ToList();
        if (pallets.Count == 0) return null;
        if (byPallet) return string.Join("、", pallets);
        return string.Join("、", pallets.Select(pid => world.Commands.Values.Where(c => c.Pallet == pid)
            .OrderBy(c => c.IsFinished).ThenByDescending(c => c.StatusCause).FirstOrDefault()?.Id ?? "—"));
    }

    static bool RackSlotOccupied(WorldState world, string? storageRule, PlaybackElementViewModel rack, int column, int level) =>
        StorageCode.Format(storageRule, rack.Model.Row ?? "", column, level) is { } code
        && world.Storage.TryGetValue(code, out var slot) && !slot.IsEmpty;
}
