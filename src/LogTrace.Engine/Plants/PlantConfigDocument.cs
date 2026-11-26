using System.Text.Json;
using System.Text.Json.Serialization;

namespace LogTrace.Engine.Plants;

/// <summary>Layout 元素的種類（ADR-0006 第 1 點）。JSON 中以小寫英文字串保存。</summary>
[JsonConverter(typeof(LayoutElementTypeConverter))]
public enum LayoutElementType
{
    /// <summary>
    /// Conv（有 LOG 意義，可以有別名，ADR-0026）：本公司的輸送設備；原本的站點（station）與原本的 Conv（dock）合併而成。
    /// 設定檔寫 conv；讀到 station、dock 時都視為 Conv。
    /// </summary>
    Conv,
    /// <summary>料架（含儲位；有 LOG 意義）。</summary>
    Rack,
    /// <summary>搬送機（有 LOG 意義，可以有別名）。</summary>
    Vehicle,
    /// <summary>輸送線（輔助）：表示兩個 Conv、工站點、EQ 或搬送機之間的貨物流向，單向或雙向（ADR-0006 第 1 點）。</summary>
    Conveyor,
    /// <summary>區域（輔助）。</summary>
    Area,
    /// <summary>文字（輔助）。</summary>
    Label,
    /// <summary>防火門（有 LOG 意義，可以有別名）：開或關；可以關聯穿過它的輸送線（ADR-0006 第 1 點 2026-11-06 補充）。</summary>
    FireDoor,
    /// <summary>導軌（輔助）：搬送機行走的路徑，由直線與弧線組成，可以閉合（ADR-0006 第 14 點）。</summary>
    Rail,
    /// <summary>電梯（有 LOG 意義，可以有別名）：在樓層之間搬送貨物；和搬送機是不同的種類（ADR-0006 第 15 點）。</summary>
    Elevator,
    /// <summary>RM（有 LOG 意義，可以有別名，ADR-0019）：只在 Conv 與料架之間搬運；可以指定所走的導軌。</summary>
    Rm,
    /// <summary>工站點（有 LOG 意義，可以有別名，ADR-0020）：一端給使用者操作的設備（例如堆高機），另一端接 Conv；狀態推導比照站點。</summary>
    Workstation,
    /// <summary>圍欄（輔助，ADR-0020）：沿折線的黃色網狀圍欄，只幫助辨識位置，不推導狀態。</summary>
    Fence,
    /// <summary>圍欄門（輔助，ADR-0020）：放在圍欄缺口的門；LOG 沒有記錄開關與模式，不推導狀態。</summary>
    FenceGate,
    /// <summary>EQ（有 LOG 意義，可以有別名，ADR-0026）：其他公司的設備（LOG 中的 EQxxx）；貨物在這裡交出。門朝緊鄰的 Conv（ADR-0020 補充）。</summary>
    Eq,
}

/// <summary>
/// 元素種類的 JSON 字串（ADR-0026）：寫出 camelCase；讀取時 station 與 dock 都讀成 Conv。
/// 要排在 JsonStringEnumConverter 之前（設定的 Converters 優先於型別上的屬性）。
/// </summary>
public sealed class LayoutElementTypeConverter : JsonConverter<LayoutElementType>
{
    public override LayoutElementType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("元素種類必須是文字");
        var text = reader.GetString() ?? "";
        if (text is "station" or "dock") return LayoutElementType.Conv;
        foreach (var t in Enum.GetValues<LayoutElementType>())
            if (string.Equals(JsonNamingPolicy.CamelCase.ConvertName(t.ToString()), text, StringComparison.Ordinal)) return t;
        throw new JsonException($"不認得的元素種類：{text}");
    }

    public override void Write(Utf8JsonWriter writer, LayoutElementType value, JsonSerializerOptions options) =>
        writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
}

/// <summary>雙深料架的深淺（ADR-0019）：離所屬 RM 近的為淺。JSON 中為 shallow、deep。</summary>
public enum RowDepth
{
    Shallow,
    Deep,
}

/// <summary>
/// 排的配置表的一列（ADR-0019 第 3 點，選填）：排號、所屬的 RM（元素 id）；雙深時再填深淺與配對的排號（同一側相鄰、同列同層）。
/// </summary>
public sealed record LayoutRow(string Row, string? Rm = null, RowDepth? Depth = null, string? Pair = null);

/// <summary>
/// 同一樓層中的層段（上層／下層）：元素畫在樓層地板（下層）或上層地板（樓層地板 + 上層高度）。JSON 中為 upper；下層不寫出。
/// </summary>
public enum LayoutDeck
{
    Lower,
    Upper,
}

/// <summary>防火門的開關狀態。JSON 中以小寫英文字串保存（open、closed）。</summary>
public enum DoorState
{
    Open,
    Closed,
}

/// <summary>
/// Layout 的一個元素（ADR-0006）。單位為公尺；x、y 為未旋轉時左上角的位置，w 為寬、h 為深，
/// 旋轉以中心點為準，只允許 0、90、180、270 度。用不到的欄位為 null，不寫進檔案。
/// </summary>
public sealed class LayoutElement
{
    public string Id { get; set; } = "";
    public LayoutElementType Type { get; set; }
    public string? Name { get; set; }

    /// <summary>所屬樓層的名稱（ADR-0006 第 13 點）；輸送線不存（畫在兩端的樓層）。null＝第一層。</summary>
    public string? Floor { get; set; }

    /// <summary>
    /// 同一樓層中的層段：upper＝上層；null＝下層（不寫出）。輸送線不存（取起點的層段）；電梯不使用（以服務的樓層表示）。
    /// </summary>
    public LayoutDeck? Deck { get; set; }

    /// <summary>別名：只有站點、搬送機、對接設備、防火門有（ADR-0006 第 2 點）。</summary>
    public List<string>? Aliases { get; set; }

    public double? X { get; set; }
    public double? Y { get; set; }
    public double? W { get; set; }
    public double? H { get; set; }
    public int? Rotation { get; set; }

    /// <summary>高度（公尺）；料架的高度由層數 × 每層高度得出，不另外保存。</summary>
    public double? Height { get; set; }

    // 料架（ADR-0006 第 4 點）
    public string? Row { get; set; }
    public int? ColFrom { get; set; }
    public int? ColTo { get; set; }
    public int? Levels { get; set; }
    public double? LevelHeight { get; set; }

    // 輸送線：起點與終點元素的 id
    public string? From { get; set; }
    public string? To { get; set; }

    /// <summary>輸送線是否雙向；null（不寫出）表示單向，方向為 from → to。</summary>
    public bool? Bidirectional { get; set; }

    /// <summary>輸送線連到搬送機時，那一端的交接點（公尺）；線畫到交接點，不畫到搬送機中心（ADR-0006 第 7 點）。</summary>
    public LayoutPoint? FromPoint { get; set; }
    public LayoutPoint? ToPoint { get; set; }

    // 防火門（ADR-0006 第 1 點 2026-11-06 補充）

    /// <summary>平時的狀態（在編輯器手動設定）；回放時還沒有 LOG 事件時以它為「推估」。null＝不設定。</summary>
    public DoorState? DoorState { get; set; }

    /// <summary>穿過這扇防火門的輸送線 id；門關上時這些輸送線畫成阻斷。</summary>
    public List<string>? Conveyors { get; set; }

    // 導軌（ADR-0006 第 14 點）：點與各段的凸度；不存 x、y、w、h、rotation

    public List<RailPoint>? Points { get; set; }

    /// <summary>頭尾相接（最後一點接回第一點）；null（不寫出）表示不閉合。</summary>
    public bool? Closed { get; set; }

    /// <summary>搬送機所走的導軌 id（ADR-0006 第 14 點）。</summary>
    public string? Rail { get; set; }

    /// <summary>電梯服務的樓層（ADR-0006 第 15 點）：在每一層同一個位置畫出來；電梯不使用 floor。</summary>
    public List<string>? ServedFloors { get; set; }

    /// <summary>自訂屬性（ADR-0006 第 6 點）：鍵＝值，值一律是文字；保持輸入順序。</summary>
    public List<KeyValuePair<string, string>>? Props { get; set; }

    /// <summary>程式認不得的欄位（較新版本寫入的），存檔時原樣寫回。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public static bool CanHaveAliases(LayoutElementType type) =>
        type is LayoutElementType.Conv or LayoutElementType.Vehicle or LayoutElementType.Eq or LayoutElementType.FireDoor or LayoutElementType.Elevator
            or LayoutElementType.Rm or LayoutElementType.Workstation;

    /// <summary>可以當作輸送線兩端的元素：Conv、工站點（ADR-0020）、EQ（ADR-0026）、搬送機。電梯不接輸送線（ADR-0006）。</summary>
    public static bool CanBeConveyorEnd(LayoutElementType type) =>
        type is LayoutElementType.Conv or LayoutElementType.Workstation or LayoutElementType.Eq or LayoutElementType.Vehicle;

    /// <summary>狀態推導比照 Conv（原本的站點）的種類：Conv 與工站點（ADR-0020、ADR-0026）。</summary>
    public static bool IsStationLike(LayoutElementType type) => type is LayoutElementType.Conv or LayoutElementType.Workstation;

    /// <summary>以折線（points）表示的種類：導軌與圍欄（ADR-0020）。</summary>
    public static bool HasPath(LayoutElementType type) => type is LayoutElementType.Rail or LayoutElementType.Fence;
}

/// <summary>Layout 上的一個點（公尺）。</summary>
public sealed record LayoutPoint(double X, double Y);

/// <summary>Layout 的幾何計算。</summary>
public static class LayoutGeometry
{
    /// <summary>元素在畫面上的範圍（考慮旋轉：90、270 度時寬與深對調，中心不變）。</summary>
    public static (double Left, double Top, double Right, double Bottom) Bounds(double x, double y, double w, double h, int rotation)
    {
        var (cx, cy) = (x + w / 2, y + h / 2);
        var (hw, hh) = rotation is 90 or 270 ? (h / 2, w / 2) : (w / 2, h / 2);
        return (cx - hw, cy - hh, cx + hw, cy + hh);
    }

    /// <summary>元素邊緣上離 (tx, ty) 最近的點：在外面時取最近的邊緣點，在裡面時投影到最近的一邊。</summary>
    public static LayoutPoint NearestEdgePoint(double x, double y, double w, double h, int rotation, double tx, double ty)
    {
        var (l, t, r, b) = Bounds(x, y, w, h, rotation);
        var (px, py) = (Math.Clamp(tx, l, r), Math.Clamp(ty, t, b));
        if (px > l && px < r && py > t && py < b)
        {
            // 在裡面：移到最近的一邊
            var d = new[] { (px - l, 0), (r - px, 1), (py - t, 2), (b - py, 3) }.MinBy(v => v.Item1).Item2;
            (px, py) = d switch { 0 => (l, py), 1 => (r, py), 2 => (px, t), _ => (px, b) };
        }
        return new LayoutPoint(Math.Round(px, 3), Math.Round(py, 3));
    }

    /// <summary>兩個矩形是否碰到（含邊緣相接）。</summary>
    public static bool RectsTouch((double Left, double Top, double Right, double Bottom) a, (double Left, double Top, double Right, double Bottom) b) =>
        a.Left <= b.Right && b.Left <= a.Right && a.Top <= b.Bottom && b.Top <= a.Bottom;

    /// <summary>線段是否碰到矩形（含端點在矩形內）；Liang–Barsky 裁切。</summary>
    public static bool SegmentTouchesRect(double x1, double y1, double x2, double y2, (double Left, double Top, double Right, double Bottom) rect)
    {
        double t0 = 0, t1 = 1, dx = x2 - x1, dy = y2 - y1;
        foreach (var (p, q) in new[] { (-dx, x1 - rect.Left), (dx, rect.Right - x1), (-dy, y1 - rect.Top), (dy, rect.Bottom - y1) })
        {
            if (p == 0)
            {
                if (q < 0) return false;
                continue;
            }
            var u = q / p;
            if (p < 0) t0 = Math.Max(t0, u);
            else t1 = Math.Min(t1, u);
            if (t0 > t1) return false;
        }
        return true;
    }
}

/// <summary>樓層（ADR-0006 第 13 點）：名稱與順序；每個元素屬於一層，輸送線畫在兩端的樓層。</summary>
public static class LayoutFloors
{
    /// <summary>舊設定檔沒有樓層時的預設樓層。</summary>
    public const string Default = "1F";

    /// <summary>樓層名稱的比對：大小寫視為相同（和別名一致）。</summary>
    public static StringComparer NameComparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>舊設定檔或沒有填樓板高度時的預設（公尺，ADR-0017 第 6 點）。</summary>
    public const double DefaultHeight = 5;

    /// <summary>樓層名稱清單；沒有時為只有 1F。</summary>
    public static IReadOnlyList<string> Of(LayoutSection layout) =>
        layout.Floors is { Count: > 0 } floors ? floors.Select(f => f.Name).ToList() : [Default];

    /// <summary>樓層的樓板高度（公尺）：有填時用填的值，沒有時為 <see cref="DefaultHeight"/>。</summary>
    public static double HeightOf(LayoutSection layout, string floor) =>
        layout.Floors?.FirstOrDefault(f => NameComparer.Equals(f.Name, floor))?.Height ?? DefaultHeight;

    /// <summary>
    /// 每層地板的高度（公尺，最低層為 0）：依樓層順序，把下面各層的樓板高度相加（3D 回放一律上下堆疊，ADR-0017 第 6 點）。
    /// </summary>
    public static IReadOnlyList<(string Floor, double Elevation, double Height)> Stack(LayoutSection layout)
    {
        var list = new List<(string, double, double)>();
        double z = 0;
        foreach (var f in Of(layout))
        {
            var h = HeightOf(layout, f);
            list.Add((f, z, h));
            z += h;
        }
        return list;
    }

    /// <summary>
    /// 元素所在的樓層：輸送線取起點的樓層（沒有起點時取終點）；其他元素取 floor，沒有時為第一層。
    /// 清單中大小寫不同的名稱以清單中的寫法為準。
    /// </summary>
    public static string FloorOf(LayoutElement e, IReadOnlyList<string> floors, Func<string?, LayoutElement?> find)
    {
        if (e.Type == LayoutElementType.Conveyor)
            return find(e.From) is { } a && a.Type != LayoutElementType.Conveyor ? FloorOf(a, floors, find)
                : find(e.To) is { } b && b.Type != LayoutElementType.Conveyor ? FloorOf(b, floors, find)
                : floors[0];
        return Canonical(e.Floor, floors) ?? e.Floor ?? floors[0];
    }

    /// <summary>
    /// 元素畫在哪些樓層：電梯為服務的樓層（清單中的寫法，依樓層順序）；其他元素為 <see cref="FloorOf"/> 的那一層。
    /// </summary>
    public static IReadOnlyList<string> FloorsOf(LayoutElement e, IReadOnlyList<string> floors, Func<string?, LayoutElement?> find) =>
        e.Type == LayoutElementType.Elevator
            ? floors.Where(f => (e.ServedFloors ?? []).Any(s => NameComparer.Equals(s.Trim(), f))).ToList()
            : [FloorOf(e, floors, find)];

    /// <summary>清單中與 name 相同（大小寫視為相同）的名稱；沒有時為 null。</summary>
    public static string? Canonical(string? name, IReadOnlyList<string> floors) =>
        name is null ? null : floors.FirstOrDefault(f => NameComparer.Equals(f, name.Trim()));

    /// <summary>
    /// 樓層的上層高度（上層地板離樓層地板的距離，公尺）：有填時用填的值，沒有時為樓板高度的一半。
    /// </summary>
    public static double UpperHeightOf(LayoutSection layout, string floor) =>
        layout.Floors?.FirstOrDefault(f => NameComparer.Equals(f.Name, floor))?.UpperHeight ?? DefaultUpperHeight(HeightOf(layout, floor));

    /// <summary>沒有填上層高度時的預設：樓板高度的一半。</summary>
    public static double DefaultUpperHeight(double floorHeight) => floorHeight / 2;

    /// <summary>
    /// 元素所在的層段：輸送線取起點的層段（沒有起點時取終點）；電梯一律為下層（以服務的樓層表示）；其他元素取 deck，沒有時為下層。
    /// </summary>
    public static LayoutDeck DeckOf(LayoutElement e, Func<string?, LayoutElement?> find)
    {
        if (e.Type == LayoutElementType.Elevator) return LayoutDeck.Lower;
        if (e.Type == LayoutElementType.Conveyor)
            return find(e.From) is { } a && a.Type != LayoutElementType.Conveyor ? DeckOf(a, find)
                : find(e.To) is { } b && b.Type != LayoutElementType.Conveyor ? DeckOf(b, find)
                : LayoutDeck.Lower;
        return e.Deck ?? LayoutDeck.Lower;
    }

    /// <summary>層段的地板離樓層地板的高度（公尺）：下層為 0，上層為 <see cref="UpperHeightOf"/>。</summary>
    public static double DeckBase(LayoutSection layout, string floor, LayoutDeck deck) =>
        deck == LayoutDeck.Upper ? UpperHeightOf(layout, floor) : 0;

    /// <summary>層段的名稱（畫面與訊息用）。</summary>
    public static string DeckLabel(LayoutDeck deck) => deck == LayoutDeck.Upper ? "上層" : "下層";

    /// <summary>存檔前補齊：一律寫出樓層清單，每個元素（輸送線除外）都寫出樓層（ADR-0006 第 13 點）。</summary>
    public static void Normalize(LayoutSection layout)
    {
        var floors = Of(layout).ToList();
        if (layout.Floors is not { Count: > 0 }) layout.Floors = [Default];
        foreach (var e in layout.Elements)
        {
            if (e.Type == LayoutElementType.Elevator)
            {
                e.Floor = null; // 電梯以服務的樓層表示
                e.Deck = null;
                e.ServedFloors = e.ServedFloors?.Select(f => Canonical(f, floors) ?? f).ToList();
                continue;
            }
            e.Floor = e.Type == LayoutElementType.Conveyor ? null : Canonical(e.Floor, floors) ?? e.Floor ?? floors[0];
            if (e.Type == LayoutElementType.Conveyor || e.Deck == LayoutDeck.Lower) e.Deck = null; // 下層不寫出；輸送線取起點的層段
        }
    }
}

/// <summary>
/// 一個樓層：名稱、樓板高度（公尺；null＝沒有填，使用 <see cref="LayoutFloors.DefaultHeight"/>）與上層高度
/// （上層地板離樓層地板的距離，公尺；null＝沒有填，使用 <see cref="LayoutFloors.DefaultUpperHeight"/>）。
/// </summary>
public sealed record LayoutFloor(string Name, double? Height = null, double? UpperHeight = null)
{
    /// <summary>只有名稱的樓層（舊設定檔的寫法）。</summary>
    public static implicit operator LayoutFloor(string name) => new(name);
}

/// <summary>
/// 樓層清單的讀寫：讀取時接受名稱（舊版）或 {name, height, upperHeight} 物件；寫出時只要有一層填了高度或上層高度就全部寫成物件，否則寫成名稱。
/// </summary>
public sealed class LayoutFloorListConverter : JsonConverter<List<LayoutFloor>>
{
    public override List<LayoutFloor> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("樓層必須是陣列。");
        var list = new List<LayoutFloor>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                list.Add(new LayoutFloor(reader.GetString()!));
                continue;
            }
            if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("樓層必須是名稱或 {name, height}。");
            string? name = null;
            double? height = null, upper = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var key = reader.GetString();
                reader.Read();
                if (string.Equals(key, "name", StringComparison.OrdinalIgnoreCase))
                    name = reader.TokenType == JsonTokenType.String ? reader.GetString() : throw new JsonException("樓層的 name 必須是文字。");
                else if (string.Equals(key, "height", StringComparison.OrdinalIgnoreCase))
                    height = reader.TokenType switch
                    {
                        JsonTokenType.Number => reader.GetDouble(),
                        JsonTokenType.Null => null,
                        _ => throw new JsonException("樓層的 height 必須是數字（公尺）。"),
                    };
                else if (string.Equals(key, "upperHeight", StringComparison.OrdinalIgnoreCase))
                    upper = reader.TokenType switch
                    {
                        JsonTokenType.Number => reader.GetDouble(),
                        JsonTokenType.Null => null,
                        _ => throw new JsonException("樓層的 upperHeight 必須是數字（公尺）。"),
                    };
                else reader.Skip();
            }
            list.Add(new LayoutFloor(name ?? throw new JsonException("樓層缺少 name。"), height, upper));
        }
        return list;
    }

    public override void Write(Utf8JsonWriter writer, List<LayoutFloor> value, JsonSerializerOptions options)
    {
        bool objects = value.Any(f => f.Height is not null || f.UpperHeight is not null);
        writer.WriteStartArray();
        foreach (var f in value)
        {
            if (!objects)
            {
                writer.WriteStringValue(f.Name);
                continue;
            }
            writer.WriteStartObject();
            writer.WriteString("name", f.Name);
            if (f.Height is { } h) writer.WriteNumber("height", h);
            if (f.UpperHeight is { } u) writer.WriteNumber("upperHeight", u);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
}

/// <summary>Layout 區段（ADR-0006 第 7 點）。</summary>
public sealed class LayoutSection
{
    /// <summary>座標單位，固定為公尺。</summary>
    public string Units { get; set; } = "m";

    /// <summary>儲位編號的拆解規則，例如 RRCCCLL（排 2 碼、列 3 碼、層 2 碼）。</summary>
    public string? StorageCodeRule { get; set; }

    /// <summary>
    /// 樓層，順序即樓層順序（ADR-0006 第 13 點）；null 或空＝只有 1F。每層可以填樓板高度（ADR-0017 第 6 點）：
    /// 沒有填任何高度時寫成名稱的陣列（與舊版相同），有填時寫成 {name, height} 的物件陣列（格式版本 6）。
    /// </summary>
    [JsonConverter(typeof(LayoutFloorListConverter))]
    public List<LayoutFloor>? Floors { get; set; }

    public List<LayoutElement> Elements { get; set; } = [];

    /// <summary>排的配置表（ADR-0019，選填）；null 或空＝單深、排不屬於任何 RM。</summary>
    public List<LayoutRow>? Rows { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// 廠別設定檔（ADR-0006 第 7 點、ADR-0002 第 7 點）：一個廠別一個 JSON 檔，記錄格式版本、修訂號、修改人與修改時間。
/// 目前的區段：模組順序、已知 LOG 種類、Layout。時區、解析規則等區段在後續 ticket 加入；
/// 程式認不得的欄位會原樣保留。
/// </summary>
public sealed class PlantConfigDocument
{
    /// <summary>
    /// 程式能讀寫的格式版本。2：輸送線的 bidirectional 與交接點（#63）。3：防火門（舊版程式不認得 fireDoor 這個種類）。
    /// 4：導軌（#100，舊版程式不認得 rail 這個種類）。5：電梯（#102）。6：樓板高度（#113，樓層寫成物件，舊版程式讀不了）。
    /// 7：RM 與排的配置表（#123，ADR-0019）。8：工站點、圍欄、圍欄門（ADR-0020）。9：EQ（ADR-0020 補充）。
    /// 10：同一樓層的上層與下層（元素的 deck、樓層的 upperHeight；舊版程式會把上層元素畫在下層、存檔時遺失上層高度）。
    /// 11：站點與原本的 Conv（dock）合併為 Conv，type 寫 conv（ADR-0026；舊版程式不認得 conv 這個種類）。
    /// 存檔時只寫實際用到的最低版本，讓舊版程式照常開啟（ADR-0006 第 7 點）。
    /// </summary>
    public const int CurrentFormatVersion = 11;

    /// <summary>
    /// 這份設定需要的格式版本：有 Conv 時為 11；有上層元素或填了上層高度時為 10；有 EQ 時為 9；有工站點、圍欄、圍欄門時為 8；有 RM 或排的配置表時為 7；有填樓板高度時為 6；有電梯時為 5；有導軌時為 4；有防火門時為 3；有雙向或連到搬送機（有交接點）的輸送線時為 2；否則為 1。
    /// </summary>
    public int RequiredFormatVersion() =>
        Layout.Elements.Any(e => e.Type == LayoutElementType.Conv) ? 11
        : Layout.Elements.Any(e => e.Deck == LayoutDeck.Upper) || Layout.Floors?.Any(f => f.UpperHeight is not null) == true ? 10
        : Layout.Elements.Any(e => e.Type == LayoutElementType.Eq) ? 9
        : Layout.Elements.Any(e => e.Type is LayoutElementType.Workstation or LayoutElementType.Fence or LayoutElementType.FenceGate) ? 8
        : Layout.Elements.Any(e => e.Type == LayoutElementType.Rm) || Layout.Rows is { Count: > 0 } ? 7
        : Layout.Floors?.Any(f => f.Height is not null) == true ? 6
        : Layout.Elements.Any(e => e.Type == LayoutElementType.Elevator) ? 5
        : Layout.Elements.Any(e => e.Type == LayoutElementType.Rail) ? 4
        : Layout.Elements.Any(e => e.Type == LayoutElementType.FireDoor) ? 3
        : Layout.Elements.Any(e => e.Type == LayoutElementType.Conveyor && (e.Bidirectional == true || e.FromPoint is not null || e.ToPoint is not null)) ? 2 : 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public string Plant { get; set; } = "";
    public int Revision { get; set; }
    public string? RevisedBy { get; set; }
    public DateTimeOffset? RevisedAt { get; set; }

    public List<string> ModuleOrder { get; set; } = [.. PlantConfig.Default.ModuleOrder];
    public List<string> KnownLoggers { get; set; } = [.. PlantConfig.Default.KnownLoggers];

    /// <summary>依模組與 Logger 指定編碼（UTF-8 或 Big5）；沒有指定的檔案自動判斷。</summary>
    public List<EncodingRule> Encodings { get; set; } = [];

    /// <summary>解析欄位的抽取規則（ADR-0005 第 8 點）；新的設定從初始規則開始。</summary>
    public List<ParseRule> ParseRules { get; set; } = [.. PlantConfig.DefaultParseRules];

    /// <summary>代表「在席」的訊號名稱（在席指示燈）。</summary>
    public string PresenceSignal { get; set; } = PlantConfig.Default.PresenceSignal;

    /// <summary>
    /// 站點模式代號的名稱（ADR-0005 第 3 點）：代號＝名稱，保持輸入順序。回放畫面顯示名稱，沒填的代號直接顯示。
    /// 真實的代號意義只填在本機的設定檔。
    /// </summary>
    public List<KeyValuePair<string, string>> StationModeNames { get; set; } = [];

    /// <summary>
    /// 沒有 LOG 的門檻（ADR-0006、ADR-0008 第 3 點，#84）：模組＝秒數，保持輸入順序。
    /// 沒有列出的模組使用預設 300 秒；0 表示該模組不標示。
    /// </summary>
    public List<KeyValuePair<string, int>> GapThresholds { get; set; } = [];

    /// <summary>
    /// 可能卡住的門檻秒數（ADR-0005 第 5 點 2026-11-16 補充，#142）：命令某一段或站點讀到棧板後，超過這麼久沒有下一步時標示「可能卡住（推估）」。
    /// null 表示預設 300 秒；0 表示不判定。舊版程式不認得這個欄位時略過，不影響開啟，所以不升格式版本。
    /// </summary>
    public int? StallThresholdSeconds { get; set; }

    /// <summary>實際使用的可能卡住門檻秒數。</summary>
    public int EffectiveStallThresholdSeconds => StallThresholdSeconds ?? Playback.Stalls.DefaultThresholdSeconds;

    /// <summary>個人資料的遮罩規則（正規表示式，ADR-0009 第 1 點）。</summary>
    public List<string> MaskPatterns { get; set; } = [.. Search.TextMasker.DefaultPatterns];

    /// <summary>廠別的時區（ADR-0003 第 1 點），預設台北。</summary>
    public string TimeZone { get; set; } = PlantConfig.DefaultTimeZone;

    public LayoutSection Layout { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>匯入與排序使用的部分。</summary>
    public PlantConfig ToPlantConfig() => new()
    {
        Name = Plant, Revision = Revision, ModuleOrder = [.. ModuleOrder], KnownLoggers = [.. KnownLoggers], Encodings = [.. Encodings],
        ParseRules = [.. ParseRules], PresenceSignal = PresenceSignal, MaskPatterns = [.. MaskPatterns],
        TimeZone = string.IsNullOrWhiteSpace(TimeZone) ? PlantConfig.DefaultTimeZone : TimeZone.Trim(),
    };
}
