using System.Text.RegularExpressions;
using LogTrace.Engine.Plants;

namespace LogTrace.Engine.Parsing;

/// <summary>解析欄位的種類（ADR-0005 第 8 點）。抽取規則以正規表示式的具名群組表示，群組名稱就是欄位種類。</summary>
public static class FieldKinds
{
    /// <summary>Conv 的編號（ADR-0026；原本的站點，舊名稱 station）。</summary>
    public const string Conv = "conv";
    public const string Vehicle = "vehicle";
    /// <summary>EQ（其他公司的設備）的編號（ADR-0026；舊名稱 dock）。</summary>
    public const string Eq = "eq";
    /// <summary>防火門的編號（LOG 樣式待確認，ADR-0006 第 1 點 2026-11-06 補充）。</summary>
    public const string FireDoor = "fireDoor";
    /// <summary>電梯的編號（LOG 樣式待確認，ADR-0005 第 11 點）；不和搬送機共用。</summary>
    public const string Elevator = "elevator";
    /// <summary>RM 的編號（ADR-0019，#123 使用者決定另設欄位）；不和搬送機（STV）共用。LOG 樣式待確認。</summary>
    public const string Rm = "rm";
    public const string Storage = "storage";
    public const string Command = "command";
    public const string CommandType = "commandType";
    public const string Segment = "segment";
    public const string Source = "source";
    public const string Destination = "destination";
    public const string Pallet = "pallet";
    public const string Bin = "bin";
    public const string Signal = "signal";
    public const string SignalValue = "signalValue";
    public const string StationMode = "stationMode";
    /// <summary>Conv 的運轉狀態（例如自動、手動），照 LOG 的文字（ADR-0005 第 3 點，#70）。</summary>
    public const string StationStatus = "stationStatus";
    public const string Alarm = "alarm";
    public const string AlarmCode = "alarmCode";
    public const string Online = "online";
    public const string Mode = "mode";
    /// <summary>BCR（條碼讀取器）的名稱（ADR-0027），對應到 BCR 元素的別名。</summary>
    public const string Bcr = "bcr";
    /// <summary>BCR 讀到或手動輸入的條碼；一次多個時以逗號分隔（ADR-0027）。</summary>
    public const string Barcode = "barcode";
    /// <summary>讀取失敗的錯誤代碼（ADR-0027）。</summary>
    public const string ErrorCode = "errorCode";
    /// <summary>磅秤的編號（ADR-0027），對應到磅秤元素的別名。</summary>
    public const string Scale = "scale";
    /// <summary>磅秤讀到的重量（單位視為 kg，假設；ADR-0027）。</summary>
    public const string Weight = "weight";
    /// <summary>重量上限、下限（ADR-0027）；0 或空白表示沒有上下限。</summary>
    public const string MaxWeight = "maxWeight";
    public const string MinWeight = "minWeight";

    /// <summary>所有欄位種類與中文名稱（依畫面顯示順序）。</summary>
    public static IReadOnlyList<(string Kind, string Label)> All { get; } =
    [
        (Conv, "Conv"), (Vehicle, "搬送機"), (Eq, "EQ"), (FireDoor, "防火門"), (Elevator, "電梯"), (Rm, "RM"), (Storage, "儲位"),
        (Command, "命令"), (CommandType, "命令類型"), (Segment, "段"), (Source, "來源"), (Destination, "目的地"),
        (Pallet, "棧板"), (Bin, "桶"),
        (Signal, "訊號"), (SignalValue, "訊號值"), (StationMode, "Conv 模式"), (StationStatus, "運轉狀態"),
        (Alarm, "警報"), (AlarmCode, "警報代碼"), (Online, "連線"), (Mode, "模式"),
        (Bcr, "BCR"), (Barcode, "條碼"), (ErrorCode, "錯誤代碼"), (Scale, "磅秤"), (Weight, "重量"), (MaxWeight, "重量上限"), (MinWeight, "重量下限"),
    ];

    /// <summary>可以對應到 Layout 元素別名的欄位（ADR-0006 第 2、3 點）。</summary>
    public static IReadOnlyList<string> Identifiers { get; } = [Conv, Vehicle, Eq, FireDoor, Elevator, Rm, Bcr, Scale];

    /// <summary>
    /// 舊的欄位種類名稱（ADR-0026）：規則的群組名稱與已建立的案件資料庫中仍可能是舊名稱，
    /// 抽取與讀取時對應到新名稱；正規表示式與案件資料庫本身不改寫。
    /// </summary>
    public static IReadOnlyDictionary<string, string> Legacy { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["station"] = Conv,
        ["dock"] = Eq,
    };

    /// <summary>新名稱（舊名稱對應到新名稱，其他原樣）。</summary>
    public static string Canonical(string kind) => Legacy.TryGetValue(kind, out var k) ? k : kind;

    /// <summary>新名稱與對應到它的舊名稱（查詢舊案件資料庫用）。</summary>
    public static IEnumerable<string> WithLegacy(IEnumerable<string> kinds)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in kinds)
        {
            var c = Canonical(k);
            set.Add(c);
            foreach (var (old, now) in Legacy)
                if (now == c) set.Add(old);
        }
        return set;
    }

    public static bool IsKnown(string kind) => All.Any(k => k.Kind == Canonical(kind));

    public static string Label(string kind) => All.FirstOrDefault(k => k.Kind == Canonical(kind)).Label ?? kind;
}

/// <summary>
/// 規則可以宣告的「效果」：這一行 LOG 代表哪一種狀態變化（ADR-0005「哪一行 LOG 代表哪一種狀態，在廠別設定中定義」）。
/// 效果的參數就是同一條規則抽出的欄位。
/// </summary>
public static class EffectKinds
{
    public const string Signal = "signal";
    public const string StationMode = "stationMode";
    /// <summary>Conv 的運轉狀態（#70）。</summary>
    public const string StationStatus = "stationStatus";
    public const string PalletRead = "palletRead";
    /// <summary>棧板加入一個桶（ADR-0005 第 2 點 2026-11-15 補充）：桶加到棧板的桶清單（去除重複），不移動棧板。</summary>
    public const string BinAdded = "binAdded";
    public const string CommandCreated = "commandCreated";
    public const string SegmentDispatched = "segmentDispatched";
    public const string SegmentArrived = "segmentArrived";
    /// <summary>命令的一段作業完成，LOG 沒有記錄地點（例如放進儲位的完成回報，#53）。</summary>
    public const string SegmentFinished = "segmentFinished";
    public const string CommandCompleted = "commandCompleted";

    /// <summary>命令取消（LOG 樣式待確認，ADR-0005 第 6 點；以可設定的規則處理）。</summary>
    public const string CommandCancelled = "commandCancelled";
    public const string Connection = "connection";
    public const string AlarmRaised = "alarmRaised";
    public const string AlarmCleared = "alarmCleared";

    /// <summary>防火門開、防火門關（LOG 樣式待確認；由廠別設定的規則決定哪一行是哪一種）。</summary>
    public const string FireDoorOpened = "fireDoorOpened";
    public const string FireDoorClosed = "fireDoorClosed";

    /// <summary>電梯的效果（ADR-0005 第 11 點；LOG 樣式待確認）：派給電梯、電梯抵達、電梯作業完成。</summary>
    public const string ElevatorDispatched = "elevatorDispatched";
    public const string ElevatorArrived = "elevatorArrived";
    public const string ElevatorFinished = "elevatorFinished";

    /// <summary>BCR 與磅秤的效果（ADR-0027）：開始讀取、停止讀取、讀到條碼、讀取失敗、手動輸入、磅秤讀值、重量上下限。</summary>
    public const string BcrReadStart = "bcrReadStart";
    public const string BcrReadStop = "bcrReadStop";
    public const string BcrRead = "bcrRead";
    public const string BcrFailed = "bcrFailed";
    public const string BcrManual = "bcrManual";
    public const string ScaleRead = "scaleRead";
    public const string WeightLimits = "weightLimits";

    /// <summary>效果的名稱，以及規則必須有的欄位（「A|B」表示其中之一）。</summary>
    public static IReadOnlyList<(string Effect, string Label, string[] Required)> All { get; } =
    [
        (Signal, "Conv 訊號 ON／OFF（在席訊號決定在席燈）", [FieldKinds.Conv, FieldKinds.Signal, FieldKinds.SignalValue]),
        (StationMode, "Conv 模式", [FieldKinds.Conv, FieldKinds.StationMode]),
        (StationStatus, "Conv 運轉狀態（自動／手動等）", [FieldKinds.Conv, FieldKinds.StationStatus]),
        (PalletRead, "在 Conv 讀到棧板（與桶）", [FieldKinds.Conv, FieldKinds.Pallet]),
        (BinAdded, "棧板加入一個桶（每行一個桶，累加）", [FieldKinds.Pallet, FieldKinds.Bin]),
        (CommandCreated, "建立命令", [FieldKinds.Command]),
        (SegmentDispatched, "命令的一段派給搬送機或 RM", [FieldKinds.Command, $"{FieldKinds.Vehicle}|{FieldKinds.Rm}", FieldKinds.Destination]),
        (SegmentArrived, "命令的一段抵達目的地", [FieldKinds.Command, FieldKinds.Destination]),
        (SegmentFinished, "命令的一段作業完成（LOG 沒有記錄地點）", [FieldKinds.Command]),
        (CommandCompleted, "命令完成", [FieldKinds.Command]),
        (CommandCancelled, "命令取消（樣式待確認）", [FieldKinds.Command]),
        (Connection, "搬送機或 RM 的連線狀態或自動／手動", [$"{FieldKinds.Vehicle}|{FieldKinds.Rm}", $"{FieldKinds.Online}|{FieldKinds.Mode}"]),
        (AlarmRaised, "警報發生", [$"{FieldKinds.Vehicle}|{FieldKinds.Conv}|{FieldKinds.Eq}|{FieldKinds.Elevator}|{FieldKinds.Rm}"]),
        (AlarmCleared, "警報復歸", [$"{FieldKinds.Vehicle}|{FieldKinds.Conv}|{FieldKinds.Eq}|{FieldKinds.Elevator}|{FieldKinds.Rm}"]),
        (FireDoorOpened, "防火門開（樣式待確認）", [FieldKinds.FireDoor]),
        (FireDoorClosed, "防火門關（樣式待確認）", [FieldKinds.FireDoor]),
        (ElevatorDispatched, "命令的一段派給電梯（樣式待確認）", [FieldKinds.Command, FieldKinds.Elevator, FieldKinds.Destination]),
        (ElevatorArrived, "電梯抵達目的地（樣式待確認）", [FieldKinds.Command, FieldKinds.Destination]),
        (ElevatorFinished, "電梯作業完成（LOG 沒有記錄地點，樣式待確認）", [FieldKinds.Command]),
        (BcrReadStart, "BCR 開始讀取", [$"{FieldKinds.Bcr}|{FieldKinds.Conv}"]),
        (BcrReadStop, "BCR 停止讀取", [$"{FieldKinds.Bcr}|{FieldKinds.Conv}"]),
        (BcrRead, "BCR 讀到條碼（多個以逗號分隔）", [$"{FieldKinds.Bcr}|{FieldKinds.Conv}", FieldKinds.Barcode]),
        (BcrFailed, "BCR 讀取失敗或逾時", [$"{FieldKinds.Bcr}|{FieldKinds.Conv}"]),
        (BcrManual, "手動輸入條碼", [$"{FieldKinds.Bcr}|{FieldKinds.Conv}", FieldKinds.Barcode]),
        (ScaleRead, "磅秤讀值（單位視為 kg）", [$"{FieldKinds.Scale}|{FieldKinds.Conv}", FieldKinds.Weight]),
        (WeightLimits, "棧板的重量上下限（0 或空白＝沒有上下限）", [FieldKinds.Pallet, $"{FieldKinds.MaxWeight}|{FieldKinds.MinWeight}"]),
    ];

    public static bool IsKnown(string effect) => All.Any(e => e.Effect == effect);

    public static string Label(string effect) => All.FirstOrDefault(e => e.Effect == effect).Label ?? effect;
}

/// <summary>一筆事件的一個解析欄位（參照表的一列；同一事件可以有多個同種類的值）。</summary>
public readonly record struct EventField(long Sequence, string Kind, string Value);

/// <summary>一筆事件的一個效果，與它的參數（欄位種類 → 值；重複的群組可以有多個值，例如多個桶）。</summary>
public sealed record EventEffect(long Sequence, string Effect, IReadOnlyDictionary<string, IReadOnlyList<string>> Params)
{
    public string? Get(string kind) => Params.TryGetValue(kind, out var v) && v.Count > 0 ? v[0] : null;

    public IReadOnlyList<string> GetAll(string kind) => Params.TryGetValue(kind, out var v) ? v : [];
}

/// <summary>
/// 依廠別設定的規則從事件訊息抽出解析欄位與效果（ADR-0004 第 4 點、ADR-0005 第 8 點）。
/// 每條規則是一個正規表示式，限定模組或 Logger 時只套用到符合的事件；一個事件中每個符合處都會抽出，
/// 同一事件內相同的（種類、值）只記一次。重複的群組（例如 (?:(?&lt;bin&gt;…) )+）的每一次都會抽出。
/// 規則有宣告效果時，每個符合處產生一個效果，參數為該符合處的欄位。
/// </summary>
public sealed class FieldExtractor
{
    /// <summary>單一訊息比對的時間上限，避免寫壞的規則讓匯入卡住。</summary>
    static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(200);

    readonly List<(ParseRule Rule, Regex Regex, string[] Groups)> _rules;

    public FieldExtractor(IEnumerable<ParseRule> rules)
    {
        _rules = rules.Select(r =>
        {
            var regex = Compile(r.Pattern);
            return (r, regex, regex.GetGroupNames().Where(FieldKinds.IsKnown).ToArray());
        }).ToList();
    }

    public static Regex Compile(string pattern) =>
        new(pattern, RegexOptions.CultureInvariant | RegexOptions.Multiline, MatchTimeout);

    /// <summary>
    /// 檢查規則：正規表示式可以編譯，至少有一個已知的欄位群組，沒有未知的具名群組；
    /// 有效果時，效果要認得，且規則有效果需要的欄位。傳回問題說明；沒有問題時為 null。
    /// </summary>
    public static string? Check(ParseRule rule)
    {
        Regex regex;
        try
        {
            regex = Compile(rule.Pattern);
        }
        catch (ArgumentException ex)
        {
            return $"規則「{rule.Name}」的正規表示式有誤：{ex.Message}";
        }
        var named = regex.GetGroupNames().Where(g => !int.TryParse(g, out _)).ToList();
        var kinds = named.Select(FieldKinds.Canonical).ToList(); // 舊的群組名稱 station、dock 視為 conv、eq（ADR-0026）
        var unknown = named.Where(g => !FieldKinds.IsKnown(g)).ToList();
        if (unknown.Count > 0)
            return $"規則「{rule.Name}」有不認得的欄位：{string.Join("、", unknown)}；可以用的欄位：{string.Join("、", FieldKinds.All.Select(k => k.Kind))}。";
        if (named.Count == 0)
            return $"規則「{rule.Name}」沒有任何欄位（具名群組，例如 (?<conv>…)）。";
        if (rule.Effect is { } effect)
        {
            if (!EffectKinds.IsKnown(effect))
                return $"規則「{rule.Name}」的效果「{effect}」不認得；可以用的效果：{string.Join("、", EffectKinds.All.Select(e => e.Effect))}。";
            var missing = EffectKinds.All.Single(e => e.Effect == effect).Required
                .Where(req => !req.Split('|').Any(kinds.Contains)).ToList();
            if (missing.Count > 0)
                return $"規則「{rule.Name}」的效果「{effect}」需要欄位：{string.Join("、", missing.Select(m => m.Replace("|", " 或 ")))}。";
        }
        return null;
    }

    public IReadOnlyList<(string Kind, string Value)> Extract(string module, string logger, string message) =>
        ExtractWithEffects(0, module, logger, message).Fields.Select(f => (f.Kind, f.Value)).ToList();

    /// <summary>抽出一筆事件的欄位與效果。</summary>
    public (List<EventField> Fields, List<EventEffect> Effects) ExtractWithEffects(long sequence, string module, string logger, string message)
    {
        var fields = new List<EventField>();
        var effects = new List<EventEffect>();
        foreach (var (rule, regex, groups) in _rules)
        {
            if (rule.Module is { } m && !string.Equals(m, module, StringComparison.OrdinalIgnoreCase)) continue;
            if (rule.Logger is { } l && !string.Equals(l, logger, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                foreach (Match match in regex.Matches(message))
                {
                    var values = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
                    foreach (var g in groups)
                    {
                        var captured = match.Groups[g].Captures.Select(c => c.Value).Where(v => v.Length > 0).Distinct().ToList();
                        if (captured.Count == 0) continue;
                        var kind = FieldKinds.Canonical(g); // 舊的群組名稱（ADR-0026）
                        values[kind] = values.TryGetValue(kind, out var had) ? [.. had.Union(captured)] : captured;
                        foreach (var v in captured)
                            if (!fields.Contains(new EventField(sequence, kind, v))) fields.Add(new EventField(sequence, kind, v));
                    }
                    if (rule.Effect is { } effect) effects.Add(new EventEffect(sequence, effect, values));
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // 規則太慢時略過這一筆事件；不中斷匯入
            }
        }
        return (fields, effects);
    }

    /// <summary>為一批事件抽出所有解析欄位。</summary>
    public List<EventField> ExtractAll(IEnumerable<Events.LogEvent> events) => ExtractAllWithEffects(events).Fields;

    /// <summary>為一批事件抽出所有解析欄位與效果（依事件序號）。</summary>
    public (List<EventField> Fields, List<EventEffect> Effects) ExtractAllWithEffects(IEnumerable<Events.LogEvent> events)
    {
        var fields = new List<EventField>();
        var effects = new List<EventEffect>();
        foreach (var e in events)
        {
            var (f, x) = ExtractWithEffects(e.Sequence, e.Module, e.Logger, e.Message);
            fields.AddRange(f);
            effects.AddRange(x);
        }
        return (fields, effects);
    }
}
