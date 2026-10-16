using System.Text;
using LogTrace.Engine.Cases;
using LogTrace.Engine.Events;
using LogTrace.Engine.Parsing;
using LogTrace.Engine.Plants;
using LogTrace.Tests.Support;

namespace LogTrace.Engine.Tests;

// 案件依所選的廠別設定匯入（#34）。內容全部是虛構的。
public sealed class PlantSelectionTests : IDisposable
{
    readonly TempFolder _tmp = new();
    readonly CaseLibrary _library;

    public PlantSelectionTests() => _library = new CaseLibrary(Path.Combine(_tmp.Path, "Cases"));

    public void Dispose() => _tmp.Dispose();

    static List<LogEvent> AllEvents(Case c) => c.GetEvents(1, int.MaxValue).ToList();

    static PlantConfig Plant(Action<PlantConfigDocument> edit)
    {
        var doc = new PlantConfigDocument { Plant = "範例廠（虛構）", Revision = 3 };
        edit(doc);
        return doc.ToPlantConfig();
    }

    /// <summary>
    /// 很短的 Big5 檔：選用的字（Big5 前導位元組 0xC2～0xDF、第二位元組 0xA1～0xBF）剛好也是合法的 UTF-8，
    /// 自動判斷會誤認為 UTF-8（ADR-0004 風險）。訊息為虛構。
    /// </summary>
    const string AmbiguousText = "警譯讀聽";

    string WriteAmbiguousBig5Log()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(950).GetBytes($"2030-01-09 08:00:00.0000 | DEBUG | PLCLOG | [A-01] {AmbiguousText}\r\n\r\n");
        Assert.True(System.Text.Unicode.Utf8.IsValid(bytes)); // 確認這份資料真的會讓自動判斷誤認
        var dir = Path.Combine(_tmp.Path, "src", "20300109", "PLC-2030-01-09");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "PLCLOG.txt"), bytes);
        return Path.Combine(_tmp.Path, "src");
    }

    [Fact]
    public void 案件記錄所屬廠別與修訂號_重新開啟後相同()
    {
        var plant = Plant(d => d.Encodings = [new("PLC", "PLCLOG", "Big5")]);
        string folder;
        using (var c = _library.Create("甲", plant)) folder = c.Folder;

        using var opened = Case.Open(folder);

        Assert.Equal("範例廠（虛構）", opened.Info.Plant.Name);
        Assert.Equal(3, opened.Info.Plant.Revision);
        Assert.Equal([new EncodingRule("PLC", "PLCLOG", "Big5")], opened.Info.Plant.Encodings);
        Assert.Equal("範例廠（虛構）", _library.List().Single().PlantName);
        Assert.Contains("範例廠（虛構）（修訂 3）", opened.GetAudit()[0].Detail);
    }

    [Fact]
    public void 很短的Big5檔被誤認為UTF8時_在廠別設定指定Big5後可以正確解碼()
    {
        var source = WriteAmbiguousBig5Log();
        using var auto = _library.Create("自動判斷", PlantConfig.Default);
        using var specified = _library.Create("指定編碼", Plant(d => d.Encodings = [new("PLC", "PLCLOG", "Big5")]));

        auto.Import(source);
        specified.Import(source);

        Assert.DoesNotContain(AmbiguousText, AllEvents(auto).Single().Message); // 自動判斷誤認為 UTF-8，出現亂碼且沒有標示
        Assert.Equal(LogEncoding.Utf8Name, auto.GetFiles().Single().Encoding);
        Assert.Equal($"[A-01] {AmbiguousText}", AllEvents(specified).Single().Message);
        Assert.Equal(LogEncoding.Big5Name, specified.GetFiles().Single().Encoding);
        Assert.Equal(AmbiguousText, specified.ReadRawLines("20300109/PLC-2030-01-09/PLCLOG.txt", 1, 1)[0].Text[^AmbiguousText.Length..]);
    }

    [Fact]
    public void 不限模組的編碼規則_適用所有模組_模組相符的規則優先()
    {
        var plant = Plant(d => d.Encodings = [new(null, "DBUPDATE_LOG", "Big5"), new("EXE", "DBUPDATE_LOG", "UTF-8")]);

        Assert.Equal("UTF-8", plant.EncodingFor("EXE", "dbupdate_log"));
        Assert.Equal("Big5", plant.EncodingFor("Server", "DBUPDATE_LOG"));
        Assert.Null(plant.EncodingFor("PLC", "PLCLOG"));
    }

    [Fact]
    public void 匯入使用所選廠別的模組順序()
    {
        using var defaults = _library.Create("預設順序", PlantConfig.Default);
        using var plcFirst = _library.Create("PLC 優先", Plant(d => d.ModuleOrder = ["PLC", "EXE", "Server"]));

        defaults.Import(TempFolder.Fixture("two-modules"));
        plcFirst.Import(TempFolder.Fixture("two-modules"));

        // 09:00:00.0000 同時間戳記的 EXE 與 PLC：依廠別設定的模組順序
        Assert.Equal(["EXE", "PLC"], AllEvents(defaults).Take(2).Select(e => e.Module));
        Assert.Equal(["PLC", "EXE"], AllEvents(plcFirst).Take(2).Select(e => e.Module));
    }

    [Fact]
    public void 匯入使用所選廠別的已知LOG種類()
    {
        using var defaults = _library.Create("預設", PlantConfig.Default);
        using var known = _library.Create("已知", Plant(d => d.KnownLoggers.Add("MysteryLOG")));

        defaults.Import(TempFolder.Fixture("parsing-cases"));
        known.Import(TempFolder.Fixture("parsing-cases"));

        static LogEvent Mystery(Case c) => AllEvents(c).Single(e => e.Logger == "MysteryLOG");
        Assert.True(Mystery(defaults).Flags.HasFlag(EventFlags.UnknownKind));
        Assert.False(Mystery(known).Flags.HasFlag(EventFlags.UnknownKind));
    }

    [Fact]
    public void 廠別設定檔_編碼設定的讀寫往返_與檢查()
    {
        var doc = new PlantConfigDocument { Plant = "範例廠（虛構）", Encodings = [new("PLC", "PLCLOG", "Big5"), new(null, "WCSCMD", "UTF-8")] };
        var path = Path.Combine(_tmp.Path, "編碼.json");

        PlantConfigFile.Save(doc, path);
        var loaded = PlantConfigFile.Load(path);

        Assert.Equal(PlantConfigFile.Serialize(doc), PlantConfigFile.Serialize(loaded));
        Assert.Equal(doc.Encodings, loaded.Encodings);
        Assert.DoesNotContain("\"module\": null", File.ReadAllText(path));

        loaded.Encodings.Add(new("plc", "plclog", "Big5"));
        loaded.Encodings.Add(new(null, "BCR_LOG", "Shift_JIS"));
        loaded.Encodings.Add(new(null, " ", "Big5"));
        loaded.ModuleOrder.Add("plc");
        var issues = PlantConfigFile.Validate(loaded).Select(i => i.Message).ToList();

        Assert.Contains(issues, m => m.Contains("PLC / PLCLOG") && m.Contains("重複"));
        Assert.Contains(issues, m => m.Contains("Shift_JIS") && m.Contains("不支援"));
        Assert.Contains(issues, m => m.Contains("沒有填 Logger"));
        Assert.Contains(issues, m => m.Contains("模組順序"));
    }
}
