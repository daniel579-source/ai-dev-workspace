using System.Text.Json;
using System.Text.Json.Serialization;

namespace LogTrace.Engine.Plants;

/// <summary>Layout 元素的種類（ADR-0006 第 1 點）。JSON 中以小寫英文字串保存。</summary>
public enum LayoutElementType
{
    /// <summary>站點（有 LOG 意義，可以有別名）。</summary>
    Station,
    /// <summary>料架（含儲位；有 LOG 意義）。</summary>
    Rack,
    /// <summary>搬送機（有 LOG 意義，可以有別名）。</summary>
    Vehicle,
    /// <summary>對接設備（有 LOG 意義，可以有別名）。</summary>
    Dock,
    /// <summary>輸送線（輔助）：表示兩個站點、對接設備或搬送機之間的貨物流向，單向或雙向（ADR-0006 第 1 點）。</summary>
    Conveyor,
    /// <summary>區域（輔助）。</summary>
    Area,
    /// <summary>文字（輔助）。</summary>
    Label,
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

    /// <summary>別名：只有站點、搬送機、對接設備有（ADR-0006 第 2 點）。</summary>
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

    /// <summary>自訂屬性（ADR-0006 第 6 點）：鍵＝值，值一律是文字；保持輸入順序。</summary>
    public List<KeyValuePair<string, string>>? Props { get; set; }

    /// <summary>程式認不得的欄位（較新版本寫入的），存檔時原樣寫回。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public static bool CanHaveAliases(LayoutElementType type) =>
        type is LayoutElementType.Station or LayoutElementType.Vehicle or LayoutElementType.Dock;

    /// <summary>可以當作輸送線兩端的元素：站點、對接設備、搬送機。</summary>
    public static bool CanBeConveyorEnd(LayoutElementType type) =>
        type is LayoutElementType.Station or LayoutElementType.Dock or LayoutElementType.Vehicle;
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
}

/// <summary>Layout 區段（ADR-0006 第 7 點）。</summary>
public sealed class LayoutSection
{
    /// <summary>座標單位，固定為公尺。</summary>
    public string Units { get; set; } = "m";

    /// <summary>儲位編號的拆解規則，例如 RRCCCLL（排 2 碼、列 3 碼、層 2 碼）。</summary>
    public string? StorageCodeRule { get; set; }

    public List<LayoutElement> Elements { get; set; } = [];

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
    /// 程式能讀寫的格式版本。2：輸送線的 bidirectional 與交接點（#63）。
    /// 存檔時只有用到版本 2 的功能才寫 2，其他仍寫 1，讓舊版程式照常開啟（ADR-0006 第 7 點）。
    /// </summary>
    public const int CurrentFormatVersion = 2;

    /// <summary>這份設定需要的格式版本：有雙向或連到搬送機（有交接點）的輸送線時為 2，否則為 1。</summary>
    public int RequiredFormatVersion() =>
        Layout.Elements.Any(e => e.Type == LayoutElementType.Conveyor && (e.Bidirectional == true || e.FromPoint is not null || e.ToPoint is not null)) ? 2 : 1;

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
