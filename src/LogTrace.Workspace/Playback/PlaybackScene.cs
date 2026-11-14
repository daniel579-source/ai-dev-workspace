using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using LogTrace.Engine.Playback;
using LogTrace.Engine.Plants;

namespace LogTrace.Workspace.Playback;

// 3D 回放的場景資料（ADR-0017 第 4 點）：C# 把 Layout 元素與目前時刻的狀態以 JSON 傳給網頁，
// 網頁（three.js）只負責畫出來與回報點選，不推導狀態。座標單位為公尺：x、y 為 Layout 平面（y 向下），z 為高度。

/// <summary>一個樓層：名稱、地板高度（下面各層樓板高度的和）與樓板高度。</summary>
public sealed record SceneFloor(string Name, double Elevation, double Height);

/// <summary>
/// 場景中的一個元素（靜態部分）。X、Y、W、H、Rotation 與 Layout 相同（左上角、寬、深、順時針角度）；Height 為元素高度，
/// 沒有填時依種類使用預設值（<see cref="PlaybackScene.DefaultHeight"/>）。Path 為導軌的折線或輸送線的兩端（公尺）。
/// </summary>
public sealed record SceneElement(
    string Id, string Type, string Name, string Floor,
    double X, double Y, double W, double H, int Rotation, double Height,
    int? Levels = null, double? LevelHeight = null, int? ColFrom = null, int? ColTo = null,
    IReadOnlyList<double[]>? Path = null, bool Closed = false, bool Bidirectional = false,
    IReadOnlyList<string>? ServedFloors = null);

/// <summary>場景的靜態部分；載入案件時傳一次。</summary>
public sealed record SceneLayout(IReadOnlyList<SceneFloor> Floors, IReadOnlyList<SceneElement> Elements)
{
    public static SceneLayout Empty { get; } = new([new SceneFloor(LayoutFloors.Default, 0, LayoutFloors.DefaultHeight)], []);
}

/// <summary>
/// 元素在目前時刻的狀態。Look 為圖例的顏色（<see cref="ElementLook"/>）。
/// X、Y：沿導軌移動的搬送機的位置（中心，公尺；推估時 Estimated）；其他元素為 null。
/// ElevatorFloor：電梯目前的樓層；移動中時為 null，改用 MovingFrom、MovingTo（推估）。
/// RackSlots：料架有貨的儲位（[列, 層]）。
/// </summary>
public sealed record SceneElementState(
    string Id, string Look, bool Selected = false,
    string? Presence = null, string? Badge = null, bool Inconsistent = false, bool Estimated = false,
    bool Blocked = false, bool BlockedEstimated = false, bool Loaded = false, bool Executing = false,
    string? Summary = null, double? X = null, double? Y = null,
    string? ElevatorFloor = null, string? MovingFrom = null, string? MovingTo = null,
    IReadOnlyList<int[]>? RackSlots = null, int? SelectedColumn = null);

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
    /// 站點為低平台、搬送機為低矮的軌道車、對接設備中等高度、防火門為門板；料架依層數 × 每層高度。
    /// </summary>
    public static double DefaultHeight(LayoutElementType type) => type switch
    {
        LayoutElementType.Station => 0.8,
        LayoutElementType.Vehicle => 0.6,
        LayoutElementType.Dock => 1.5,
        LayoutElementType.FireDoor => 3,
        LayoutElementType.Conveyor => 0.6,
        LayoutElementType.Rack => 1,
        LayoutElementType.Elevator => 1.2, // 台車的高度；井道依服務的樓層
        LayoutElementType.Label => 2,
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

    public static SceneLayout LayoutOf(LayoutSection layout, IEnumerable<PlaybackElementViewModel> elements)
    {
        var floors = LayoutFloors.Stack(layout).Select(f => new SceneFloor(f.Floor, f.Elevation, f.Height)).ToList();
        var list = new List<SceneElement>();
        foreach (var e in elements)
        {
            var m = e.Model;
            var type = JsonNamingPolicy.CamelCase.ConvertName(m.Type.ToString());
            switch (m.Type)
            {
                case LayoutElementType.Rail:
                {
                    var path = RailPath.Of(m).Polyline().Select(p => new[] { p.X, p.Y }).ToList();
                    list.Add(new SceneElement(e.Id, type, e.Name, e.Floor, 0, 0, 0, 0, 0, DefaultHeight(m.Type), Path: path, Closed: m.Closed == true));
                    break;
                }
                case LayoutElementType.Conveyor:
                {
                    if (e.ConveyorEnds is not { } ends) break; // 兩端找不到時不畫（存檔時已檢查）
                    list.Add(new SceneElement(e.Id, type, e.Name, e.Floor, 0, 0, 0, 0, 0, m.Height ?? DefaultHeight(m.Type),
                        Path: [[ends.From.X, ends.From.Y], [ends.To.X, ends.To.Y]], Bidirectional: m.Bidirectional == true));
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
                        ServedFloors: e.IsElevator ? e.ServedFloors : null));
                    break;
            }
        }
        return new SceneLayout(floors, list);
    }

    /// <summary>目前時刻的狀態：沿用 Layout 回放面板已推導的元素狀態（<see cref="PlaybackViewModel"/>），不另外推導。</summary>
    public static SceneState StateOf(IEnumerable<PlaybackElementViewModel> elements, WorldState world, PlaybackElementViewModel? selected, RackCellViewModel? selectedCell, string? storageRule)
    {
        var list = new List<SceneElementState>();
        foreach (var e in elements)
        {
            var look = JsonNamingPolicy.CamelCase.ConvertName(e.Look.ToString());
            bool isSelected = ReferenceEquals(e, selected);
            switch (e.Type)
            {
                case LayoutElementType.Vehicle:
                {
                    world.Vehicles.TryGetValue(e.Id, out var v);
                    list.Add(new SceneElementState(e.Id, look, isSelected, Inconsistent: e.IsInconsistent, Estimated: e.IsEstimated,
                        Loaded: world.PalletsAt(LocationKind.Vehicle, e.Id).Any(), Executing: v?.Work == VehicleWork.Executing,
                        Summary: e.Summary, X: e.Placement?.X, Y: e.Placement?.Y));
                    break;
                }
                case LayoutElementType.Elevator:
                {
                    world.Elevators.TryGetValue(e.Id, out var ev);
                    bool moving = ev?.IsMoving == true;
                    list.Add(new SceneElementState(e.Id, look, isSelected, Estimated: e.IsEstimated,
                        Loaded: world.PalletsAt(LocationKind.Elevator, e.Id).Any(), Executing: moving, Summary: e.Summary,
                        ElevatorFloor: moving ? null : ev?.Floor, MovingFrom: moving ? ev!.FromFloor : null, MovingTo: moving ? ev!.ToFloor : null));
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
                case LayoutElementType.Station:
                    list.Add(new SceneElementState(e.Id, look, isSelected, Presence: e.PresenceLight switch { "●" => "on", "○" => "off", _ => "unknown" },
                        Badge: e.StatusBadge, Inconsistent: e.IsInconsistent, Loaded: e.Look == ElementLook.Occupied || world.PalletsAt(LocationKind.Station, e.Id).Any(),
                        Summary: e.Summary));
                    break;
                default:
                    list.Add(new SceneElementState(e.Id, look, isSelected, Inconsistent: e.IsInconsistent, Estimated: e.IsEstimated,
                        Blocked: e.IsBlocked, BlockedEstimated: e.IsBlockedEstimated, Summary: e.Summary));
                    break;
            }
        }
        return new SceneState(world.Sequence, list);
    }

    static bool RackSlotOccupied(WorldState world, string? storageRule, PlaybackElementViewModel rack, int column, int level) =>
        StorageCode.Format(storageRule, rack.Model.Row ?? "", column, level) is { } code
        && world.Storage.TryGetValue(code, out var slot) && !slot.IsEmpty;
}
