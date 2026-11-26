using LogTrace.Engine.Parsing;
using LogTrace.Engine.Playback;
using LogTrace.Engine.Plants;

namespace LogTrace.Engine.Tests;

// BCR 與磅秤（ADR-0027）：讀取中、讀到、手動、失敗、停止讀取沒有回傳；Conv 對應；桶條碼加入棧板；磅秤等待中與上下限比對。
// 以下 LOG、編號、條碼與重量全部是虛構的。
public sealed class BcrScaleTests
{
    static LayoutSection Layout() => new()
    {
        Elements =
        [
            new() { Id = "st-1", Type = LayoutElementType.Conv, Name = "入庫口1", Aliases = ["A-01"], X = 0, Y = 0, W = 2, H = 2, Rotation = 0 },
            new() { Id = "st-2", Type = LayoutElementType.Conv, Name = "入庫口2", Aliases = ["A-02"], X = 4, Y = 0, W = 2, H = 2, Rotation = 0 },
            new() { Id = "bc-1", Type = LayoutElementType.Bcr, Name = "棧板 BCR 1", Owner = "st-1", X = 2.1, Y = 0, W = 0.5, H = 0.5, Rotation = 0 },
            new() { Id = "bc-2", Type = LayoutElementType.Bcr, Name = "桶 BCR 1", Owner = "st-1", Reads = BcrReads.Bin, Aliases = ["A-01_DRUM"], X = 2.1, Y = 1, W = 0.5, H = 0.5, Rotation = 0 },
            new() { Id = "sc-1", Type = LayoutElementType.Scale, Name = "磅秤 1", Owner = "st-1", X = 0, Y = 2.5, W = 1.5, H = 1.5, Rotation = 0 },
        ],
    };

    static EventEffect E(long seq, string effect, params (string Kind, string Value)[] p) =>
        new(seq, effect, p.GroupBy(x => x.Kind).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Value).ToList()));

    static WorldModel Model(params EventEffect[] effects) => new(effects, Layout(), "在席");

    [Fact]
    public void 讀取器名稱與Conv相同時_對應到所屬的棧板BCR_讀取中從第一次開始讀取算起()
    {
        var m = Model(
            E(1, EffectKinds.BcrReadStart, (FieldKinds.Bcr, "A-01")),
            E(2, EffectKinds.BcrReadStart, (FieldKinds.Bcr, "A-01")),
            E(3, EffectKinds.BcrRead, (FieldKinds.Bcr, "A-01"), (FieldKinds.Barcode, "PLT-EX-0001")));

        Assert.Equal(1, m.StateAt(2).Bcrs["bc-1"].ReadingSince);
        Assert.False(m.StateAt(2).Bcrs.ContainsKey("bc-2"));
        var b = m.StateAt(3).Bcrs["bc-1"];
        Assert.Null(b.ReadingSince);
        Assert.Equal(BcrResult.Read, b.Result);
        Assert.Equal(["PLT-EX-0001"], b.Barcodes);
    }

    [Fact]
    public void 讀取失敗算異常_停止讀取沒有回傳時保留上一次結果_手動輸入解除異常()
    {
        var m = Model(
            E(1, EffectKinds.BcrReadStart, (FieldKinds.Bcr, "A-01")),
            E(2, EffectKinds.BcrFailed, (FieldKinds.Conv, "A-01"), (FieldKinds.ErrorCode, "E901")), // PLC 的逾時只寫 Conv
            E(3, EffectKinds.BcrReadStart, (FieldKinds.Bcr, "A-01")),
            E(4, EffectKinds.BcrReadStop, (FieldKinds.Bcr, "A-01")),
            E(5, EffectKinds.BcrManual, (FieldKinds.Conv, "A-01"), (FieldKinds.Barcode, "PLT-EX-0002")));

        Assert.True(m.StateAt(2).Bcrs["bc-1"].IsFailed);
        Assert.Equal("E901", m.StateAt(2).Bcrs["bc-1"].ErrorCode);
        Assert.NotNull(m.StateAt(3).Bcrs["bc-1"].ReadingSince);
        var stopped = m.StateAt(4).Bcrs["bc-1"];
        Assert.Null(stopped.ReadingSince);
        Assert.True(stopped.IsFailed); // 停止讀取不改變結果（12a）
        var manual = m.StateAt(5).Bcrs["bc-1"];
        Assert.Equal(BcrResult.Manual, manual.Result);
        Assert.False(manual.IsFailed); // 手動輸入解除異常（11a）
        Assert.Equal([("bc-1", (string?)"E901", 2L, (long?)5L)], m.BcrFailurePeriods());
    }

    [Fact]
    public void 讀取失敗之後一直沒有讀到_異常延伸到案件結束()
    {
        var m = Model(E(1, EffectKinds.BcrFailed, (FieldKinds.Bcr, "A-01_DRUM")), E(2, EffectKinds.BcrFailed, (FieldKinds.Bcr, "A-01_DRUM")));

        Assert.Equal([("bc-2", (string?)null, 1L, (long?)null)], m.BcrFailurePeriods());
    }

    [Fact]
    public void 桶BCR的多個條碼拆開_所屬Conv上只有一個棧板時加入它的桶清單()
    {
        var m = Model(
            E(1, EffectKinds.PalletRead, (FieldKinds.Conv, "A-01"), (FieldKinds.Pallet, "P-001")),
            E(2, EffectKinds.BcrRead, (FieldKinds.Bcr, "A-01_DRUM"), (FieldKinds.Barcode, "DR-01, DR-02,DR-03 ,")),
            E(3, EffectKinds.PalletRead, (FieldKinds.Conv, "A-01"), (FieldKinds.Pallet, "P-002")),
            E(4, EffectKinds.BcrRead, (FieldKinds.Bcr, "A-01_DRUM"), (FieldKinds.Barcode, "DR-09")));

        var b = m.StateAt(2).Bcrs["bc-2"];
        Assert.Equal(["DR-01", "DR-02", "DR-03"], b.Barcodes);
        Assert.Equal("P-001", b.BinsPallet);
        Assert.Equal(["DR-01", "DR-02", "DR-03"], m.StateAt(2).Pallets["P-001"].Bins!);
        // 兩個棧板都在 Conv 上：只記錄在 BCR，不修改棧板
        Assert.Null(m.StateAt(4).Bcrs["bc-2"].BinsPallet);
        Assert.Null(m.StateAt(4).Pallets["P-002"].Bins);
        Assert.Equal(["DR-01", "DR-02", "DR-03"], m.StateAt(4).Pallets["P-001"].Bins!);
    }

    [Fact]
    public void 磅秤從棧板BCR開始讀取起等待中_收到讀值或讀取失敗時結束_桶BCR不算()
    {
        var m = Model(
            E(1, EffectKinds.BcrReadStart, (FieldKinds.Bcr, "A-01_DRUM")),
            E(2, EffectKinds.BcrReadStart, (FieldKinds.Bcr, "A-01")),
            E(3, EffectKinds.ScaleRead, (FieldKinds.Conv, "A-01"), (FieldKinds.Weight, "512.5")),
            E(4, EffectKinds.BcrReadStart, (FieldKinds.Bcr, "A-01")),
            E(5, EffectKinds.BcrFailed, (FieldKinds.Conv, "A-01")));

        Assert.False(m.StateAt(1).Scales.ContainsKey("sc-1"));
        Assert.Equal(2, m.StateAt(2).Scales["sc-1"].WaitingSince);
        var s = m.StateAt(3).Scales["sc-1"];
        Assert.Null(s.WaitingSince);
        Assert.Equal(512.5, s.Weight);
        Assert.Equal(4, m.StateAt(4).Scales["sc-1"].WaitingSince);
        Assert.Null(m.StateAt(5).Scales["sc-1"].WaitingSince);
        Assert.Equal(512.5, m.StateAt(5).Scales["sc-1"].Weight); // 讀值保留
    }

    [Theory]
    [InlineData("600", "500", "100", WeightJudgement.Over)]
    [InlineData("50", "500", "100", WeightJudgement.Under)]
    [InlineData("300", "500", "100", WeightJudgement.Within)]
    [InlineData("300", "0", "0", WeightJudgement.NoLimits)] // 上下限為 0：不判定
    [InlineData("300", "", "", WeightJudgement.NoLimits)]
    [InlineData("約 300", "500", "100", WeightJudgement.NoReading)] // 不是數字
    public void 磅秤讀值與讀值棧板的上下限比對(string weight, string max, string min, WeightJudgement expected)
    {
        var m = Model(
            E(1, EffectKinds.PalletRead, (FieldKinds.Conv, "A-01"), (FieldKinds.Pallet, "P-001")),
            E(2, EffectKinds.WeightLimits, (FieldKinds.Pallet, "P-001"), (FieldKinds.MaxWeight, max), (FieldKinds.MinWeight, min)),
            E(3, EffectKinds.ScaleRead, (FieldKinds.Conv, "A-01"), (FieldKinds.Weight, weight)));

        var w = m.StateAt(3);
        Assert.Equal("P-001", w.Scales["sc-1"].Pallet);
        Assert.Equal(expected, w.Judge(w.Scales["sc-1"]));
    }

    [Theory]
    [InlineData("86]4", 864.0)]
    [InlineData("512", 512.0)]
    [InlineData("-1.5", -1.5)]
    public void 磅秤回傳值_括號外的數字接起來(string raw, double expected)
    {
        var m = Model(E(1, EffectKinds.ScaleRead, (FieldKinds.Conv, "A-01"), (FieldKinds.Weight, raw)));

        Assert.Equal(expected, m.StateAt(1).Scales["sc-1"].Weight);
        Assert.DoesNotContain("]", m.StateAt(1).Scales["sc-1"].WeightText);
    }

    [Fact]
    public void 沒有上下限資料時不判定_上下限之後才到也會比對()
    {
        var m = Model(
            E(1, EffectKinds.PalletRead, (FieldKinds.Conv, "A-01"), (FieldKinds.Pallet, "P-001")),
            E(2, EffectKinds.ScaleRead, (FieldKinds.Conv, "A-01"), (FieldKinds.Weight, "900")),
            E(3, EffectKinds.WeightLimits, (FieldKinds.Pallet, "P-001"), (FieldKinds.MaxWeight, "800")));

        Assert.Equal(WeightJudgement.NoLimits, m.StateAt(2).Judge(m.StateAt(2).Scales["sc-1"]));
        Assert.Equal(WeightJudgement.Over, m.StateAt(3).Judge(m.StateAt(3).Scales["sc-1"]));
    }

    [Fact]
    public void 沒有所屬BCR的Conv_效果不改變狀態_快照與從頭推導相同()
    {
        var effects = new[]
        {
            E(1, EffectKinds.BcrReadStart, (FieldKinds.Bcr, "A-02")),
            E(2, EffectKinds.ScaleRead, (FieldKinds.Conv, "A-02"), (FieldKinds.Weight, "1")),
            E(3, EffectKinds.BcrRead, (FieldKinds.Bcr, "A-01"), (FieldKinds.Barcode, "X")),
        };
        var m = new WorldModel(effects, Layout(), "在席", snapshotEvery: 1);

        Assert.Empty(m.StateAt(2).Bcrs);
        Assert.Empty(m.StateAt(2).Scales);
        Assert.Equal(m.ReplayFromStart(3).Fingerprint(), m.StateAt(3).Fingerprint());
    }

    [Fact]
    public void 設定檔_有BCR或磅秤時格式版本12_所屬與讀取對象存檔讀回不變()
    {
        var doc = new PlantConfigDocument { Plant = "範例廠", Layout = Layout() };

        Assert.Equal(12, doc.RequiredFormatVersion());
        var json = PlantConfigFile.Serialize(doc);
        Assert.Contains("\"type\": \"bcr\"", json);
        Assert.Contains("\"owner\": \"st-1\"", json);
        Assert.Contains("\"reads\": \"bin\"", json);
        var back = PlantConfigFile.Deserialize(json);
        Assert.Equal(BcrReads.Bin, back.Layout.Elements.Single(e => e.Id == "bc-2").Reads);
        Assert.Null(back.Layout.Elements.Single(e => e.Id == "bc-1").Reads);
        Assert.Empty(PlantConfigFile.Validate(back));

        doc.Layout.Elements.RemoveAll(e => LayoutElement.HasOwner(e.Type));
        Assert.Equal(11, doc.RequiredFormatVersion());
    }

    [Fact]
    public void 存檔前檢查_所屬Conv不存在_只有BCR與磅秤可以指定所屬_只有BCR可以設定讀取對象()
    {
        var doc = new PlantConfigDocument { Plant = "範例廠", Layout = Layout() };
        doc.Layout.Elements.Single(e => e.Id == "sc-1").Owner = "st-9";
        doc.Layout.Elements.Single(e => e.Id == "st-2").Owner = "st-1";
        doc.Layout.Elements.Single(e => e.Id == "sc-1").Reads = BcrReads.Bin;

        var issues = PlantConfigFile.Validate(doc);

        Assert.Contains(issues, i => i.Message.Contains("所屬 Conv「st-9」不存在") && i.ElementIds.SequenceEqual(["sc-1"]));
        Assert.Contains(issues, i => i.Message.Contains("只有 BCR 與磅秤可以") && i.ElementIds.SequenceEqual(["st-2"]));
        Assert.Contains(issues, i => i.Message.Contains("只有 BCR 可以") && i.ElementIds.SequenceEqual(["sc-1"]));
    }

    [Theory]
    [InlineData("BCR_LOG", "BCR_TCPIP_Socket.Send(LON),MachineName=A-01,IpAssress=192.0.2.1,IpPortNo=9000", EffectKinds.BcrReadStart, FieldKinds.Bcr, "A-01")]
    [InlineData("BCR_LOG", "BCR_TCPIP_Socket.Send(LOFF),MachineName=A-01_DRUM,IpAssress=192.0.2.2,IpPortNo=9000", EffectKinds.BcrReadStop, FieldKinds.Bcr, "A-01_DRUM")]
    [InlineData("BCR_LOG", "BCR_TCPIP_Socket.OnCommandArrived,MachineName=A-01,IpAssress=192.0.2.1,IpPortNo=9000,result=PLT-EX-0001", EffectKinds.BcrRead, FieldKinds.Barcode, "PLT-EX-0001")]
    [InlineData("BCR_LOG", "BCR_TCPIP_Socket.OnCommandArrived,MachineName=A-01,IpAssress=192.0.2.1,IpPortNo=9000,result=ERROR  ", EffectKinds.BcrFailed, FieldKinds.Bcr, "A-01")]
    [InlineData("PLCLOG", "[A-01][Station_Storage_Request_BCR] => 棧板讀取異常:BCR條碼讀取逾時(E901),清除磅秤  ", EffectKinds.BcrFailed, FieldKinds.ErrorCode, "E901")]
    [InlineData("PLCLOG", "[A-01][Station_Storage_Request_BCR] => 手動輸入的條碼,STATION_NO=A-01,SCANTYPE=BCR,PALLET=PLT-EX-0002,DRUM=DR-01", EffectKinds.BcrManual, FieldKinds.Barcode, "PLT-EX-0002")]
    [InlineData("PLCLOG", "[A-01][Station_Storage_Request_BCR] => 磅秤回傳值[512]  ", EffectKinds.ScaleRead, FieldKinds.Weight, "512]")] // 連同括號抽出，推導時去掉（#209）
    [InlineData("PLCLOG", "[A-01][Station_Storage_Request_BCR] => 磅秤回傳值[86]4  ", EffectKinds.ScaleRead, FieldKinds.Weight, "86]4")] // 一部分寫在括號外（#209）
    public void 初始規則_讀取器與PLC的紀錄產生對應的效果(string logger, string message, string effect, string kind, string value)
    {
        Assert.All(PlantConfig.DefaultParseRules, r => Assert.Null(FieldExtractor.Check(r)));

        var (_, effects) = new FieldExtractor(PlantConfig.DefaultParseRules).ExtractWithEffects(1, "PLC", logger, message);

        var x = Assert.Single(effects);
        Assert.Equal(effect, x.Effect);
        Assert.Equal(value, x.Get(kind));
    }

    [Fact]
    public void 初始規則_讀取器回傳多個桶條碼時整串抽出()
    {
        var (_, effects) = new FieldExtractor(PlantConfig.DefaultParseRules).ExtractWithEffects(1, "PLC", "BCR_LOG",
            "BCR_TCPIP_Socket.OnCommandArrived,MachineName=A-01_DRUM,IpAssress=192.0.2.2,IpPortNo=9000,result=DR-01,DR-02,DR-03");

        Assert.Equal("DR-01,DR-02,DR-03", Assert.Single(effects).Get(FieldKinds.Barcode));
    }
}
