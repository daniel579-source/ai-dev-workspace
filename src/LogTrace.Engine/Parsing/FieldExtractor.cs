using System.Text.RegularExpressions;
using LogTrace.Engine.Plants;

namespace LogTrace.Engine.Parsing;

/// <summary>解析欄位的種類（ADR-0005 第 8 點）。抽取規則以正規表示式的具名群組表示，群組名稱就是欄位種類。</summary>
public static class FieldKinds
{
    public const string Station = "station";
    public const string Vehicle = "vehicle";
    public const string Dock = "dock";
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
    public const string Alarm = "alarm";
    public const string AlarmCode = "alarmCode";
    public const string Online = "online";
    public const string Mode = "mode";

    /// <summary>所有欄位種類與中文名稱（依畫面顯示順序）。</summary>
    public static IReadOnlyList<(string Kind, string Label)> All { get; } =
    [
        (Station, "站點"), (Vehicle, "搬送機"), (Dock, "對接設備"), (Storage, "儲位"),
        (Command, "命令"), (CommandType, "命令類型"), (Segment, "段"), (Source, "來源"), (Destination, "目的地"),
        (Pallet, "棧板"), (Bin, "桶"),
        (Signal, "訊號"), (SignalValue, "訊號值"), (StationMode, "站點模式"),
        (Alarm, "警報"), (AlarmCode, "警報代碼"), (Online, "連線"), (Mode, "模式"),
    ];

    /// <summary>可以對應到 Layout 元素別名的欄位（ADR-0006 第 2、3 點）。</summary>
    public static IReadOnlyList<string> Identifiers { get; } = [Station, Vehicle, Dock];

    public static bool IsKnown(string kind) => All.Any(k => k.Kind == kind);

    public static string Label(string kind) => All.FirstOrDefault(k => k.Kind == kind).Label ?? kind;
}

/// <summary>
/// 規則可以宣告的「效果」：這一行 LOG 代表哪一種狀態變化（ADR-0005「哪一行 LOG 代表哪一種狀態，在廠別設定中定義」）。
/// 效果的參數就是同一條規則抽出的欄位。
/// </summary>
public static class EffectKinds
{
    public const string Signal = "signal";
    public const string StationMode = "stationMode";
    public const string PalletRead = "palletRead";
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

    /// <summary>效果的名稱，以及規則必須有的欄位（「A|B」表示其中之一）。</summary>
    public static IReadOnlyList<(string Effect, string Label, string[] Required)> All { get; } =
    [
        (Signal, "站點訊號 ON／OFF（在席訊號決定在席燈）", [FieldKinds.Station, FieldKinds.Signal, FieldKinds.SignalValue]),
        (StationMode, "站點模式", [FieldKinds.Station, FieldKinds.StationMode]),
        (PalletRead, "在站點讀到棧板（與桶）", [FieldKinds.Station, FieldKinds.Pallet]),
        (CommandCreated, "建立命令", [FieldKinds.Command]),
        (SegmentDispatched, "命令的一段派給搬送機", [FieldKinds.Command, FieldKinds.Vehicle, FieldKinds.Destination]),
        (SegmentArrived, "命令的一段抵達目的地", [FieldKinds.Command, FieldKinds.Destination]),
        (SegmentFinished, "命令的一段作業完成（LOG 沒有記錄地點）", [FieldKinds.Command]),
        (CommandCompleted, "命令完成", [FieldKinds.Command]),
        (CommandCancelled, "命令取消（樣式待確認）", [FieldKinds.Command]),
        (Connection, "搬送機連線狀態或自動／手動", [FieldKinds.Vehicle, $"{FieldKinds.Online}|{FieldKinds.Mode}"]),
        (AlarmRaised, "警報發生", [$"{FieldKinds.Vehicle}|{FieldKinds.Station}|{FieldKinds.Dock}"]),
        (AlarmCleared, "警報復歸", [$"{FieldKinds.Vehicle}|{FieldKinds.Station}|{FieldKinds.Dock}"]),
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
        var unknown = named.Where(g => !FieldKinds.IsKnown(g)).ToList();
        if (unknown.Count > 0)
            return $"規則「{rule.Name}」有不認得的欄位：{string.Join("、", unknown)}；可以用的欄位：{string.Join("、", FieldKinds.All.Select(k => k.Kind))}。";
        if (named.Count == 0)
            return $"規則「{rule.Name}」沒有任何欄位（具名群組，例如 (?<station>…)）。";
        if (rule.Effect is { } effect)
        {
            if (!EffectKinds.IsKnown(effect))
                return $"規則「{rule.Name}」的效果「{effect}」不認得；可以用的效果：{string.Join("、", EffectKinds.All.Select(e => e.Effect))}。";
            var missing = EffectKinds.All.Single(e => e.Effect == effect).Required
                .Where(req => !req.Split('|').Any(named.Contains)).ToList();
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
                        values[g] = captured;
                        foreach (var v in captured)
                            if (!fields.Contains(new EventField(sequence, g, v))) fields.Add(new EventField(sequence, g, v));
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
