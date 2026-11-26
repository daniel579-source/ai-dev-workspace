using LogTrace.Engine.Events;
using LogTrace.Engine.Flows;
using LogTrace.Engine.Parsing;
using LogTrace.Engine.Playback;
using LogTrace.Engine.Plants;

namespace LogTrace.Engine.Tests;

// 從正常命令學習的卡住比對（ADR-0024，#192）。編號、訊號名稱與時間全部是虛構的。
public sealed class NormalFlowTests
{
    static readonly DateTime T0 = new(2030, 1, 7, 8, 0, 0);
    static readonly DateTimeOffset Now = new(2030, 2, 1, 9, 0, 0, TimeSpan.Zero);
    const string Plant = "範例廠";

    static readonly LayoutSection Layout = new()
    {
        Elements =
        [
            new() { Id = "st-1", Type = LayoutElementType.Conv, Aliases = ["A-01"], X = 1, Y = 1, W = 2, H = 2, Rotation = 0 },
            new() { Id = "st-2", Type = LayoutElementType.Conv, Aliases = ["A-02"], X = 5, Y = 1, W = 2, H = 2, Rotation = 0 },
            new() { Id = "st-3", Type = LayoutElementType.Conv, Aliases = ["B-01"], X = 9, Y = 1, W = 2, H = 2, Rotation = 0 },
            new() { Id = "cv-1", Type = LayoutElementType.Eq, Aliases = ["CV01"], X = 9, Y = 5, W = 2, H = 2, Rotation = 0 },
            new() { Id = "ln-1", Type = LayoutElementType.Conveyor, From = "cv-1", To = "st-3" },
            new() { Id = "vh-1", Type = LayoutElementType.Vehicle, Aliases = ["VH01"], X = 3, Y = 4, W = 1, H = 1, Rotation = 0 },
            new() { Id = "rm-1", Type = LayoutElementType.Rm, Aliases = ["RM01"], X = 3, Y = 8, W = 1, H = 1, Rotation = 0 },
        ],
    };

    /// <summary>虛構的案件：效果、時間（秒）、模組；來源檔雜湊整個案件同一個。</summary>
    sealed class FakeCase(string hash)
    {
        readonly List<(long Seq, double Sec, string Effect, (string, string)[] P, string Module)> _items = [];
        long _seq;
        public readonly List<LogGap> Gaps = [];

        public long Add(double sec, string effect, string module, params (string, string)[] p)
        {
            _items.Add((++_seq, sec, effect, p, module));
            return _seq;
        }

        public FlowCase Build(int stallThreshold = 300)
        {
            // 依時間排列後重新編號（測試中後加的警報可能比先加的命令早）
            var items = _items.OrderBy(i => i.Sec).ThenBy(i => i.Seq).Select((i, n) => i with { Seq = n + 1 }).ToList();
            var effects = items.Select(i => new EventEffect(i.Seq, i.Effect,
                i.P.GroupBy(x => x.Item1).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Item2).ToList()))).ToList();
            var times = items.ToDictionary(i => i.Seq, i => T0.AddSeconds(i.Sec));
            var modules = items.ToDictionary(i => i.Seq, i => i.Module);
            var model = new WorldModel(effects, Layout, "在席");
            Func<long, DateTime?> timeOf = s => times.TryGetValue(s, out var t) ? t : null;
            var stalls = Stalls.Find(model, timeOf, times.Values.Max(), stallThreshold, Gaps);
            return new FlowCase(model, timeOf, s => modules.GetValueOrDefault(s), _ => hash, stalls, Gaps);
        }

        /// <summary>
        /// 一個正常的 STV Conv → Conv命令（A-01 → A-02）：派送後 2 秒目的站點「置物可 ON」，5 秒來源站點「搬出完了 ON」，
        /// 30 秒抵達、35 秒完成。skip 指定要拿掉的訊號；swap 時兩個訊號順序對調。
        /// </summary>
        public void Normal(string command, double at, string? skip = null, bool swap = false, string carrier = "VH01", string to = "A-02")
        {
            Add(at, EffectKinds.CommandCreated, "WCS", (FieldKinds.Command, command), (FieldKinds.Source, "A-01"), (FieldKinds.Destination, to));
            Add(at + 1, EffectKinds.SegmentDispatched, "WCS", (FieldKinds.Command, command), (FieldKinds.Segment, "1"),
                (carrier.StartsWith("RM") ? FieldKinds.Rm : FieldKinds.Vehicle, carrier), (FieldKinds.Source, "A-01"), (FieldKinds.Destination, to));
            var signals = new List<(double, string, string)> { (3, to, "置物可"), (6, "A-01", "搬出完了") };
            if (swap) signals = [(3, "A-01", "搬出完了"), (6, to, "置物可")];
            foreach (var (sec, station, name) in signals)
                if (name != skip) Add(at + sec, EffectKinds.Signal, "PLC", (FieldKinds.Conv, station), (FieldKinds.Signal, name), (FieldKinds.SignalValue, "ON"));
            Add(at + 31, EffectKinds.SegmentArrived, "WCS", (FieldKinds.Command, command), (FieldKinds.Segment, "1"), (FieldKinds.Destination, to));
            Add(at + 36, EffectKinds.CommandCompleted, "WCS", (FieldKinds.Command, command));
        }

        /// <summary>卡住的命令：派送後只出現指定的訊號，之後 10 分鐘沒有下一步（到案件結束）。</summary>
        public void Stuck(string command, double at, bool withPlace = false, bool withOut = true, bool reversed = false)
        {
            Add(at, EffectKinds.CommandCreated, "WCS", (FieldKinds.Command, command), (FieldKinds.Source, "A-01"), (FieldKinds.Destination, "A-02"));
            Add(at + 1, EffectKinds.SegmentDispatched, "WCS", (FieldKinds.Command, command), (FieldKinds.Segment, "1"),
                (FieldKinds.Vehicle, "VH01"), (FieldKinds.Source, "A-01"), (FieldKinds.Destination, "A-02"));
            if (reversed)
            {
                Add(at + 3, EffectKinds.Signal, "PLC", (FieldKinds.Conv, "A-01"), (FieldKinds.Signal, "搬出完了"), (FieldKinds.SignalValue, "ON"));
                Add(at + 6, EffectKinds.Signal, "PLC", (FieldKinds.Conv, "A-02"), (FieldKinds.Signal, "置物可"), (FieldKinds.SignalValue, "ON"));
            }
            else
            {
                if (withPlace) Add(at + 3, EffectKinds.Signal, "PLC", (FieldKinds.Conv, "A-02"), (FieldKinds.Signal, "置物可"), (FieldKinds.SignalValue, "ON"));
                if (withOut) Add(at + 6, EffectKinds.Signal, "PLC", (FieldKinds.Conv, "A-01"), (FieldKinds.Signal, "搬出完了"), (FieldKinds.SignalValue, "ON"));
            }
            Add(at + 700, EffectKinds.Signal, "PLC", (FieldKinds.Conv, "B-01"), (FieldKinds.Signal, "在席"), (FieldKinds.SignalValue, "OFF"));
        }
    }

    /// <summary>從兩份來源 LOG 各收集 count 個正常命令，計入樣板。</summary>
    static FlowFile Learned(int countA = 3, int countB = 2)
    {
        FlowFile? file = null;
        foreach (var (hash, count) in new[] { ("HASH-A", countA), ("HASH-B", countB) }.Where(x => x.Item2 > 0))
        {
            var c = new FakeCase(hash);
            for (int i = 0; i < count; i++) c.Normal($"C-{hash[^1]}{i:00}", i * 100);
            file = NormalFlows.Add(file, Plant, 3, Layout, NormalFlows.CollectNormal(c.Build(), Layout, 3, Now), Now).File;
        }
        return file!;
    }

    [Fact]
    public void 正常命令計入樣板_以角色記錄_達成條件後可以判斷()
    {
        var file = Learned();

        Assert.Equal(5, file.Samples.Count);
        var sample = file.Samples[0];
        Assert.Equal(("VH01", "A-01", "A-02"), (sample.Carrier, sample.From, sample.To));
        Assert.Equal(["A-02 置物可 ON 2", "A-01 搬出完了 ON 5"], sample.Signals.Select(s => $"{s.Station} {s.Signal} {(s.On ? "ON" : "OFF")} {s.Offset}"));

        var row = Assert.Single(NormalFlows.Progress(file, Layout).Kinds);
        Assert.Equal(("STV Conv → Conv", FlowStatus.Ready, 5, 2, 2), (row.Kind, row.Status, row.Samples, row.Sources, row.ExpectedSignals));
        Assert.Equal(0, row.FalsePositiveRate);
    }

    [Fact]
    public void 正常命令拿掉一個訊號_卡住時找得出來_附出處()
    {
        var file = Learned();
        var c = new FakeCase("HASH-C");
        c.Normal("C-C00", 0);
        c.Stuck("C-C01", 100); // 沒有出現「置物可」
        var fc = c.Build();
        var stall = Assert.Single(fc.Stalls);

        var finding = NormalFlows.Analyze(stall, fc, file, Layout)!;

        Assert.Equal(FlowFindingKind.MissingSignal, finding.Kind);
        Assert.Contains("和過去 5 次正常的「STV Conv → Conv」相比（這是差異，不是真因）", finding.Text);
        Assert.Contains("沒有出現：目的 Conv 的 置物可 ON（正常在派送後 2～2 秒出現）", finding.Text);
        Assert.DoesNotContain("搬出完了", finding.Text);
        Assert.NotEmpty(finding.Sequences);
        Assert.DoesNotContain("真因是", finding.Text);
    }

    [Fact]
    public void 訊號都有出現時_回報沒有差異_正常的命令不被當作差異()
    {
        var file = Learned();
        var c = new FakeCase("HASH-C");
        c.Stuck("C-C01", 0, withPlace: true);
        var fc = c.Build();

        var finding = NormalFlows.Analyze(Assert.Single(fc.Stalls), fc, file, Layout)!;

        Assert.Equal(FlowFindingKind.NoDifference, finding.Kind);
    }

    [Fact]
    public void 順序與典型順序不同時_回報順序不同()
    {
        var file = Learned();
        var c = new FakeCase("HASH-C");
        c.Stuck("C-C01", 0, reversed: true);
        var fc = c.Build();

        var finding = NormalFlows.Analyze(Assert.Single(fc.Stalls), fc, file, Layout)!;

        Assert.Equal(FlowFindingKind.MissingSignal, finding.Kind);
        Assert.Contains("順序不同：目的 Conv 的 置物可 ON 正常在 來源 Conv 的 搬出完了 ON 之前，這次在之後", finding.Text);
        Assert.Equal(3, finding.Sequences.Count); // 派送與兩個訊號
    }

    [Fact]
    public void 相關模組缺LOG時_回報LOG少了資料_不回報設備少了訊號()
    {
        var file = Learned();
        var c = new FakeCase("HASH-C");
        c.Stuck("C-C01", 0);
        c.Gaps.Add(new LogGap("PLC", T0.AddSeconds(10), T0.AddSeconds(690)));
        var fc = c.Build();

        var finding = NormalFlows.Analyze(Assert.Single(fc.Stalls), fc, file, Layout)!;

        Assert.Equal(FlowFindingKind.LogMissing, finding.Kind);
        Assert.Contains("請補收 LOG 後重新匯入", finding.Text);
        Assert.Contains("模組 PLC", finding.Text);
        Assert.DoesNotContain("沒有出現", finding.Text);
    }

    [Fact]
    public void 樣本或來源不足時_顯示還不能判斷與進度()
    {
        var oneSource = Learned(countA: 6, countB: 0);
        var row = Assert.Single(NormalFlows.Progress(oneSource, Layout).Kinds);
        Assert.Equal(FlowStatus.Collecting, row.Status);
        Assert.Contains("來源 LOG 1／2", row.Note);

        var few = Learned(countA: 2, countB: 2);
        Assert.Contains("樣本 4／5", Assert.Single(NormalFlows.Progress(few, Layout).Kinds).Note);

        var c = new FakeCase("HASH-C");
        c.Stuck("C-C01", 0);
        var fc = c.Build();
        var finding = NormalFlows.Analyze(Assert.Single(fc.Stalls), fc, few, Layout)!;
        Assert.Equal(FlowFindingKind.NotYet, finding.Kind);
        Assert.Contains("收集中，還不能判斷：樣本 4／5、來源 LOG 2／2", finding.Text);

        var none = NormalFlows.Analyze(Assert.Single(fc.Stalls), fc, null, Layout)!;
        Assert.Equal(FlowFindingKind.NotYet, none.Kind);
        Assert.Contains("樣本 0／5", none.Text);
    }

    [Fact]
    public void 流程改變誤報超過10百分比時_自動退回需要重新收集_之後以新的樣本重新建立()
    {
        var file = Learned();
        Assert.True(Assert.Single(file.Kinds).Ready);

        // 設備改造後「置物可」不再出現：新來源的 5 筆樣本
        var later = Now.AddDays(1);
        var c = new FakeCase("HASH-D");
        for (int i = 0; i < 5; i++) c.Normal($"C-D{i:00}", i * 100, skip: "置物可");
        file = NormalFlows.Add(file, Plant, 3, Layout, NormalFlows.CollectNormal(c.Build(), Layout, 3, later), later).File;

        var row = Assert.Single(NormalFlows.Progress(file, Layout).Kinds);
        Assert.Equal(FlowStatus.NeedsRecollect, row.Status);
        Assert.Equal(5, row.Samples); // 只用退回後（這次計入）的樣本
        Assert.Contains("誤報超過 10%", row.Note);
        Assert.Equal(10, file.Samples.Count); // 舊樣本保留在檔案中

        // 再從另一份來源收到新流程的樣本：重新變成可以判斷，應該出現的訊號只剩「搬出完了」
        var later2 = later.AddDays(1);
        var e = new FakeCase("HASH-E");
        e.Normal("C-E00", 0, skip: "置物可");
        file = NormalFlows.Add(file, Plant, 3, Layout, NormalFlows.CollectNormal(e.Build(), Layout, 3, later2), later2).File;
        row = Assert.Single(NormalFlows.Progress(file, Layout).Kinds);
        Assert.Equal((FlowStatus.Ready, 6, 1), (row.Status, row.Samples, row.ExpectedSignals));
    }

    [Fact]
    public void 重複匯入同一份LOG_不重複計入()
    {
        var c = new FakeCase("HASH-A");
        for (int i = 0; i < 3; i++) c.Normal($"C-A{i:00}", i * 100);
        var samples = NormalFlows.CollectNormal(c.Build(), Layout, 3, Now);

        var (first, added1, dup1) = NormalFlows.Add(null, Plant, 3, Layout, samples, Now);
        var (second, added2, dup2) = NormalFlows.Add(first, Plant, 3, Layout, NormalFlows.CollectNormal(c.Build(), Layout, 3, Now.AddHours(1)), Now.AddHours(1));

        Assert.Equal((3, 0), (added1, dup1));
        Assert.Equal((0, 3), (added2, dup2));
        Assert.Equal(3, second.Samples.Count);
    }

    [Fact]
    public void 卡住_有警報_或缺LOG的命令不計入樣板()
    {
        var c = new FakeCase("HASH-A");
        c.Normal("C-A00", 0);
        c.Stuck("C-A01", 100);
        c.Normal("C-A02", 1000);
        c.Add(1002, EffectKinds.AlarmRaised, "WCS", (FieldKinds.Vehicle, "VH01"), (FieldKinds.AlarmCode, "E-999"));
        c.Add(1020, EffectKinds.AlarmCleared, "WCS", (FieldKinds.Vehicle, "VH01"));
        c.Normal("C-A03", 1100);
        c.Gaps.Add(new LogGap("PLC", T0.AddSeconds(1102), T0.AddSeconds(1500)));

        var samples = NormalFlows.CollectNormal(c.Build(), Layout, 3, Now);

        Assert.Equal(["C-A00"], samples.Select(s => s.Command));
    }

    [Fact]
    public void 對接設備的訊號以相鄰站點記錄_對應不到Layout的段落另外列出()
    {
        var c = new FakeCase("HASH-A");
        c.Add(0, EffectKinds.SegmentDispatched, "WCS", (FieldKinds.Command, "C-A00"), (FieldKinds.Vehicle, "VH01"), (FieldKinds.Source, "A-01"), (FieldKinds.Destination, "CV01"));
        c.Add(2, EffectKinds.Signal, "PLC", (FieldKinds.Conv, "B-01"), (FieldKinds.Signal, "受入可"), (FieldKinds.SignalValue, "ON"));
        c.Add(3, EffectKinds.Signal, "PLC", (FieldKinds.Conv, "A-02"), (FieldKinds.Signal, "無關"), (FieldKinds.SignalValue, "ON")); // 沒有角色，不記錄
        c.Add(20, EffectKinds.SegmentArrived, "WCS", (FieldKinds.Command, "C-A00"), (FieldKinds.Destination, "CV01"));
        c.Add(25, EffectKinds.CommandCompleted, "WCS", (FieldKinds.Command, "C-A00"));
        c.Normal("C-A01", 100, to: "Z-99"); // 目的地沒有登錄在 Layout

        var file = NormalFlows.Add(null, Plant, 3, Layout, NormalFlows.CollectNormal(c.Build(), Layout, 3, Now), Now).File;
        var (kinds, unmapped, reasons) = NormalFlows.Progress(file, Layout);

        var dock = Assert.Single(file.Samples, s => s.Command == "C-A00");
        Assert.Equal(["B-01 受入可"], dock.Signals.Select(s => $"{s.Station} {s.Signal}"));
        Assert.Equal("STV Conv → EQ", Assert.Single(kinds).Kind);
        Assert.Equal(1, unmapped);
        Assert.Equal(["目的地 Z-99 對應不到 Layout：1 段"], reasons);
    }

    [Fact]
    public void RM的段落_維持還不能判斷()
    {
        var c = new FakeCase("HASH-A");
        for (int i = 0; i < 6; i++) c.Normal($"C-A{i:00}", i * 100, carrier: "RM01");
        var d = new FakeCase("HASH-B");
        d.Normal("C-B00", 0, carrier: "RM01");
        FlowFile? file = null;
        foreach (var x in new[] { c, d }) file = NormalFlows.Add(file, Plant, 3, Layout, NormalFlows.CollectNormal(x.Build(), Layout, 3, Now), Now).File;

        var row = Assert.Single(NormalFlows.Progress(file, Layout).Kinds);
        Assert.Equal((FlowStatus.NotSupported, "RM Conv → Conv"), (row.Status, row.Kind));
        Assert.Contains("RM 的訊號對應規則待確認", row.Note);
    }

    [Fact]
    public void 兩份樣板合併_重複的來源只算一次_顯示狀態變化()
    {
        var local = Learned(countA: 3, countB: 0); // 只有來源 A：收集中
        var other = Learned(countA: 3, countB: 2); // 另一台電腦：來源 A（相同）與 B

        var preview = NormalFlows.PreviewImport(local, other, Plant, 3, Layout, Now);

        Assert.Null(preview.Error);
        Assert.Equal((2, 3), (preview.Added, preview.Duplicates));
        Assert.Equal(5, preview.Merged!.Samples.Count);
        Assert.Equal(["STV Conv → Conv：收集中 → 可以判斷"], preview.StatusChanges);
        Assert.Null(preview.RevisionNotice);
    }

    [Fact]
    public void 廠別不同時拒絕匯入()
    {
        var other = Learned() with { Plant = "別的廠" };

        var preview = NormalFlows.PreviewImport(null, other, Plant, 3, Layout, Now);

        Assert.Contains("不能匯入", preview.Error);
        Assert.Null(preview.Merged);
    }

    [Fact]
    public void 設定版本不同時_提示並以本機設定重新分類與檢驗()
    {
        // 對方的電腦：目的 Conv 的編號 X-02 是別名（本機的 Layout 沒有這個別名）
        var other = Learned();
        other = other with { PlantRevision = 7, Samples = other.Samples.Select(s => s with { To = "X-02", Signals = s.Signals.Select(x => x.Station == "A-02" ? x with { Station = "X-02" } : x).ToList() }).ToList() };

        var preview = NormalFlows.PreviewImport(null, other, Plant, 3, Layout, Now);

        Assert.Contains("修訂 7 和本機的修訂 3 不同", preview.RevisionNotice);
        var (kinds, unmapped, _) = NormalFlows.Progress(preview.Merged, Layout);
        Assert.Empty(kinds); // 不沿用對方的「可以判斷」
        Assert.Equal(5, unmapped);

        // 本機補上別名後重新分類
        var aliased = new LayoutSection { Elements = Layout.Elements.Select(e => e.Id == "st-2" ? new LayoutElement { Id = e.Id, Type = e.Type, Aliases = ["A-02", "X-02"] } : e).ToList() };
        Assert.Equal(FlowStatus.Ready, Assert.Single(NormalFlows.Progress(NormalFlows.Recalculate(preview.Merged!, aliased, Now), aliased).Kinds).Status);
    }

    [Fact]
    public void 樣板檔_讀寫往返_被別人修改時不覆寫()
    {
        var folder = Directory.CreateTempSubdirectory("flows-");
        try
        {
            var path = FlowStore.PathBeside(Path.Combine(folder.FullName, "範例廠.json"));
            Assert.EndsWith("範例廠.flows.json", path);
            Assert.Equal((null, null), FlowStore.Load(path));

            var stamp = FlowStore.Save(path, Learned(), null);
            var (loaded, loadedStamp) = FlowStore.Load(path);
            Assert.Equal(stamp, loadedStamp);
            Assert.Equal(5, loaded!.Samples.Count);
            Assert.Equal(Learned().Samples[0].Signals, loaded.Samples[0].Signals);

            // 樣板檔與總結檔不是廠別設定，不列在廠別清單中
            File.WriteAllText(Path.Combine(folder.FullName, "範例廠.json"), "{}");
            File.WriteAllText(Path.Combine(folder.FullName, "範例廠.summary.json"), "{}");
            Assert.Equal(["範例廠.json"], new PlantConfigLibrary(folder.FullName).List().Select(Path.GetFileName));

            Assert.Throws<FlowConflictException>(() => FlowStore.Save(path, loaded, null));
            File.WriteAllText(path, """{ "formatVersion": 99 }""");
            Assert.Throws<FormatException>(() => FlowStore.Load(path));
        }
        finally
        {
            folder.Delete(true);
        }
    }
}
