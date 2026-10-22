using LogTrace.Engine.Events;
using LogTrace.Engine.Plants;

namespace LogTrace.Engine.Tests;

// 沒有 LOG 的時段（#84，FR-037，ADR-0008 第 3 點）。模組與時間全部是虛構的。
public sealed class LogGapsTests
{
    static readonly DateTime T0 = new(2030, 1, 12, 8, 0, 0);

    static long _seq;

    static LogEvent E(string module, double seconds) => new(++_seq, new StableEventId(module, "20300112", "x.txt", 1),
        T0.AddSeconds(seconds), "", "DEBUG", module, module + "LOG", "虛構", new EventSource("x.txt", 1, 1, 0, 0), EventFlags.None);

    /// <summary>甲：每 30 秒一筆，10～20 分鐘之間沒有；乙：最後一筆在 24 分鐘（案件到 30 分鐘）。</summary>
    static List<LogEvent> Sample()
    {
        var events = new List<LogEvent>();
        for (int s = 0; s <= 1800; s += 30)
            if (s <= 600 || s >= 1200) events.Add(E("甲", s));
        foreach (var s in new[] { 15, 120, 270, 360, 480, 720, 900, 1080, 1260, 1440 }) events.Add(E("乙", s));
        return events.OrderBy(e => e.Timestamp).ToList();
    }

    [Fact]
    public void 相鄰事件間隔超過預設5分鐘時標示_案件結尾也標示()
    {
        var gaps = LogGaps.Find(Sample());

        Assert.Equal([new LogGap("甲", T0.AddSeconds(600), T0.AddSeconds(1200)), new LogGap("乙", T0.AddSeconds(1440), T0.AddSeconds(1800))], gaps);
        Assert.Equal("08:10:00～08:20:00，10 分鐘", gaps[0].Describe());
    }

    [Fact]
    public void 剛好等於門檻不標示_案件開頭晚於門檻才標示()
    {
        var events = new[] { E("甲", 0), E("甲", 300), E("乙", 301), E("乙", 600) };

        Assert.Equal([new LogGap("乙", T0, T0.AddSeconds(301))], LogGaps.Find(events));
    }

    [Fact]
    public void 依模組設定門檻_0表示不標示_模組名稱不分大小寫()
    {
        var events = new[] { E("PLC", 0), E("PLC", 90), E("EXE", 0), E("EXE", 1000) };

        var gaps = LogGaps.Find(events, [new("plc", 60), new("EXE", 0)]);

        Assert.Equal([new LogGap("PLC", T0, T0.AddSeconds(90)), new LogGap("PLC", T0.AddSeconds(90), T0.AddSeconds(1000))], gaps);
    }

    [Fact]
    public void 時刻在時段中_不含兩端()
    {
        var gap = new LogGap("甲", T0, T0.AddMinutes(10));

        Assert.True(gap.Contains(T0.AddMinutes(5)));
        Assert.False(gap.Contains(T0));
        Assert.False(gap.Contains(T0.AddMinutes(10)));
    }

    [Fact]
    public void 長度的寫法()
    {
        Assert.Equal("45 秒", LogGap.FormatLength(TimeSpan.FromSeconds(45)));
        Assert.Equal("6 分鐘", LogGap.FormatLength(TimeSpan.FromSeconds(390)));
        Assert.Equal("4 小時 44 分鐘", LogGap.FormatLength(TimeSpan.FromSeconds(17070)));
    }

    [Fact]
    public void 沒有事件時沒有時段()
    {
        Assert.Empty(LogGaps.Find([]));
    }

    // ---- 廠別設定 gapThresholds（ADR-0006） ----

    [Fact]
    public void 設定檔存成模組對秒數的物件_讀回後順序與內容相同()
    {
        var doc = new PlantConfigDocument { Plant = "範例廠", GapThresholds = [new("PLC", 120), new("EXE", 0)] };

        var json = PlantConfigFile.Serialize(doc);

        Assert.Contains("\"gapThresholds\": {", json);
        Assert.Contains("\"PLC\": 120", json);
        Assert.Equal([new("PLC", 120), new("EXE", 0)], PlantConfigFile.Deserialize(json).GapThresholds);
    }

    [Fact]
    public void 舊版設定檔沒有這個欄位_照常開啟()
    {
        var doc = PlantConfigFile.Deserialize("""{ "formatVersion": 1, "plant": "舊廠" }""");

        Assert.Empty(doc.GapThresholds);
        Assert.Empty(PlantConfigFile.Validate(doc));
    }

    [Fact]
    public void 秒數不是整數時_設定檔無法讀取()
    {
        var ex = Assert.Throws<PlantConfigFormatException>(() => PlantConfigFile.Deserialize("""{ "formatVersion": 1, "plant": "廠", "gapThresholds": { "PLC": 1.5 } }"""));

        Assert.Contains("必須是整數", ex.Message);
    }

    [Fact]
    public void 秒數為負_模組空白或重複時_無法存檔()
    {
        var doc = new PlantConfigDocument { Plant = "範例廠", GapThresholds = [new("PLC", -1), new(" ", 60), new("EXE", 60), new("exe", 30)] };

        var issues = PlantConfigFile.Validate(doc);

        Assert.Contains(issues, i => i.Message.Contains("「PLC」的秒數必須是 0 以上的整數"));
        Assert.Contains(issues, i => i.Message.Contains("沒有填模組"));
        Assert.Contains(issues, i => i.Message.Contains("模組「EXE」重複"));
    }

    [Fact]
    public void 門檻的模組不在模組順序中時_無法存檔()
    {
        // #86：填到 Logger 或 Layout 元件名稱時門檻不會生效，存檔時就擋下；大小寫不同視為相同模組
        var doc = new PlantConfigDocument { Plant = "範例廠", ModuleOrder = ["EXE", "PLC"], GapThresholds = [new("plc", 120), new("WCS_LOG", 60), new(" 輸送線1 ", 0)] };

        var issues = PlantConfigFile.Validate(doc).Select(i => i.Message).ToList();

        Assert.Equal(
            ["沒有 LOG 的門檻的模組「WCS_LOG」不在模組順序中，請從模組順序中選擇。", "沒有 LOG 的門檻的模組「輸送線1」不在模組順序中，請從模組順序中選擇。"],
            issues.Where(m => m.Contains("不在模組順序中")));
    }
}
