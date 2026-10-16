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
        (Command, "命令"), (CommandType, "命令類型"), (Source, "來源"), (Destination, "目的地"),
        (Pallet, "棧板"), (Bin, "桶"),
        (Signal, "訊號"), (SignalValue, "訊號值"), (StationMode, "站點模式"),
        (Alarm, "警報"), (AlarmCode, "警報代碼"), (Online, "連線"), (Mode, "模式"),
    ];

    /// <summary>可以對應到 Layout 元素別名的欄位（ADR-0006 第 2、3 點）。</summary>
    public static IReadOnlyList<string> Identifiers { get; } = [Station, Vehicle, Dock];

    public static bool IsKnown(string kind) => All.Any(k => k.Kind == kind);

    public static string Label(string kind) => All.FirstOrDefault(k => k.Kind == kind).Label ?? kind;
}

/// <summary>一筆事件的一個解析欄位（參照表的一列；同一事件可以有多個同種類的值）。</summary>
public readonly record struct EventField(long Sequence, string Kind, string Value);

/// <summary>
/// 依廠別設定的規則從事件訊息抽出解析欄位（ADR-0004 第 4 點、ADR-0005 第 8 點）。
/// 每條規則是一個正規表示式，限定模組或 Logger 時只套用到符合的事件；一個事件中每個符合處都會抽出，
/// 同一事件內相同的（種類、值）只記一次。
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

    /// <summary>檢查規則：正規表示式可以編譯，至少有一個已知的欄位群組，沒有未知的具名群組。傳回問題說明；沒有問題時為 null。</summary>
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
        return null;
    }

    public IReadOnlyList<(string Kind, string Value)> Extract(string module, string logger, string message)
    {
        var result = new List<(string, string)>();
        foreach (var (rule, regex, groups) in _rules)
        {
            if (rule.Module is { } m && !string.Equals(m, module, StringComparison.OrdinalIgnoreCase)) continue;
            if (rule.Logger is { } l && !string.Equals(l, logger, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                foreach (Match match in regex.Matches(message))
                    foreach (var g in groups)
                        if (match.Groups[g] is { Success: true, Length: > 0 } group && !result.Contains((g, group.Value)))
                            result.Add((g, group.Value));
            }
            catch (RegexMatchTimeoutException)
            {
                // 規則太慢時略過這一筆事件；不中斷匯入
            }
        }
        return result;
    }

    /// <summary>為一批事件抽出所有解析欄位。</summary>
    public List<EventField> ExtractAll(IEnumerable<Events.LogEvent> events)
    {
        var list = new List<EventField>();
        foreach (var e in events)
            foreach (var (kind, value) in Extract(e.Module, e.Logger, e.Message))
                list.Add(new EventField(e.Sequence, kind, value));
        return list;
    }
}
