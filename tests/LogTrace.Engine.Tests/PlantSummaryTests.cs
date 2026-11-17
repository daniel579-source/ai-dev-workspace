using LogTrace.Engine.Incidents;
using LogTrace.Tests.Support;

namespace LogTrace.Engine.Tests;

// 每廠總結（#168，ADR-0023 第 1 點）：統計、說明、審核、新增筆數、存檔與衝突。所有內容都是虛構的。
public sealed class PlantSummaryTests : IDisposable
{
    readonly TempFolder _tmp = new();

    public void Dispose() => _tmp.Dispose();

    static readonly DateTimeOffset T0 = new(2030, 1, 11, 9, 0, 0, TimeSpan.FromHours(8));

    static IncidentContent Content(string[] codes, string[] devices, string disposal) => new()
    {
        Symptom = "虛構現象",
        Events = [new(1, "x#1", "2030-01-11 08:00:00.000", "PLC", "虛構")],
        AlarmCodes = codes,
        Devices = devices,
        Cause = "虛構原因",
        Disposal = disposal,
        Executor = "工程師",
        Result = "虛構結果",
    };

    static IncidentRecord Reviewed(string id, string[] codes, string[] devices, string disposal) =>
        IncidentWorkflow.Approve(IncidentWorkflow.Create(IncidentSource.Case, Content(codes, devices, disposal), "甲", T0, id), "乙", T0.AddHours(1));

    static List<IncidentRecord> Sample() =>
    [
        Reviewed("IR-1", ["E101"], ["SSCA01"], "檢查行走馬達（虛構）"),
        Reviewed("IR-2", ["E101", "E301"], ["SSCA01", "ST02"], "檢查行走馬達（虛構）"),
        Reviewed("IR-3", ["E101"], ["SSCA02"], "更換減速機（虛構）"),
        Reviewed("IR-4", [], ["ST05"], "手動排除卡料（虛構）"),
        IncidentWorkflow.Create(IncidentSource.Case, Content(["E999"], ["SSCA09"], "草稿（虛構）"), "甲", T0, "IR-5"),
        IncidentWorkflow.Disable(Reviewed("IR-6", ["E888"], ["SSCA08"], "停用（虛構）"), "內容有誤", "甲", T0.AddDays(1)),
        IncidentWorkflow.MarkObsolete(Reviewed("IR-7", ["E101"], ["SSCA01"], "舊處置（虛構）"), null, "甲", T0.AddDays(1)),
    ];

    [Fact]
    public void 依有效紀錄計數與排名_排除草稿停用與已過時()
    {
        var s = PlantSummaries.Build(Sample(), "甲", T0.AddDays(2));

        Assert.Equal(["IR-1", "IR-2", "IR-3", "IR-4"], s.Covered.Select(c => c.Id));
        Assert.Equal([("E101", 3), ("E301", 1)], s.ByAlarmCode.Select(i => (i.Key, i.Count)));
        Assert.Equal([("SSCA01", 2), ("SSCA02", 1), ("ST02", 1), ("ST05", 1)], s.ByDevice.Select(i => (i.Key, i.Count)));
        Assert.Equal([(PlantSummaries.KindAlarm, 3), (PlantSummaries.KindStall, 1)], s.ByKind.Select(i => (i.Key, i.Count)));
        Assert.Equal(["IR-1", "IR-2", "IR-3"], s.ByAlarmCode[0].RecordIds);
    }

    [Fact]
    public void 處置依文字歸類並計數_不分全半形與前後空白()
    {
        var records = Sample();
        records.Add(Reviewed("IR-8", ["E101"], ["SSCA03"], " 檢查行走馬達（虛構） "));

        var e101 = PlantSummaries.Build(records, "甲", T0).ByAlarmCode[0];

        Assert.Equal("E101", e101.Key);
        Assert.Equal(("檢查行走馬達（虛構）", 3), (e101.Disposals[0].Text, e101.Disposals[0].Count));
        Assert.Equal(["IR-1", "IR-2", "IR-8"], e101.Disposals[0].RecordIds);
        Assert.Equal(("更換減速機（虛構）", 1), (e101.Disposals[1].Text, e101.Disposals[1].Count));
    }

    [Fact]
    public void 產生草稿_說明沿用目前的總結_審核通過後取代()
    {
        var f = PlantSummaries.Generate(null, "範例廠", Sample(), "甲", T0);
        Assert.Null(f.Reviewed);
        Assert.Equal("", f.Draft!.Note);

        f = PlantSummaries.SaveNote(f, "  E101 多為行走馬達問題（虛構）  ");
        Assert.Equal(["產生者與審核者同一人"], PlantSummaries.Warnings(f, "甲"));
        f = PlantSummaries.Approve(f, "乙", T0.AddHours(1));
        Assert.Null(f.Draft);
        Assert.Equal("E101 多為行走馬達問題（虛構）", f.Reviewed!.Note);
        Assert.Equal(("乙", T0.AddHours(1)), (f.Reviewer, f.ReviewedAt));
        Assert.Empty(f.ReviewWarnings);

        var again = PlantSummaries.Generate(f, "範例廠", Sample(), "乙", T0.AddDays(7));
        Assert.Equal("E101 多為行走馬達問題（虛構）", again.Draft!.Note);
        Assert.Equal(f.Reviewed, again.Reviewed); // 審核前舊的繼續有效
        Assert.Equal(f.Reviewed, PlantSummaries.DiscardDraft(again).Reviewed);
        Assert.Null(PlantSummaries.DiscardDraft(again).Draft);
    }

    [Fact]
    public void 審核的提醒_說明空白_沒有審核者不能通過_沒有草稿不能審核()
    {
        var f = PlantSummaries.Generate(null, "範例廠", Sample(), "甲", T0);

        Assert.Equal(["說明空白"], PlantSummaries.Warnings(f, "乙"));
        Assert.Equal(["說明空白"], PlantSummaries.Approve(f, "乙", T0).ReviewWarnings);
        Assert.Throws<InvalidOperationException>(() => PlantSummaries.Approve(f, " ", T0));
        Assert.Throws<InvalidOperationException>(() => PlantSummaries.Approve(PlantSummaries.DiscardDraft(f), "乙", T0));
    }

    [Fact]
    public void 之後新增或改版的已審核紀錄筆數()
    {
        var records = Sample();
        var s = PlantSummaries.Build(records, "甲", T0);
        Assert.Equal(0, PlantSummaries.NewSince(s, records));

        records.Add(Reviewed("IR-9", ["E303"], ["ST06"], "清潔鏡頭（虛構）"));
        var i = records.FindIndex(r => r.Id == "IR-1");
        var revised = IncidentWorkflow.SaveWork(IncidentWorkflow.Revise(records[i], "甲", T0.AddDays(1)),
            Content(["E101"], ["SSCA01"], "改為更換行走馬達（虛構）"), "甲", T0.AddDays(1));
        records[i] = IncidentWorkflow.Approve(revised, "乙", T0.AddDays(1));
        records[records.FindIndex(r => r.Id == "IR-3")] = IncidentWorkflow.MarkObsolete(records.Single(r => r.Id == "IR-3"), null, "甲", T0.AddDays(1));

        Assert.Equal(2, PlantSummaries.NewSince(s, records)); // IR-9 新增、IR-1 第 2 版；IR-3 已過時不計
    }

    [Fact]
    public void 經過時間的文字()
    {
        Assert.Equal("3 天", PlantSummaries.AgeText(TimeSpan.FromHours(80)));
        Assert.Equal("5 小時", PlantSummaries.AgeText(TimeSpan.FromMinutes(310)));
        Assert.Equal("12 分鐘", PlantSummaries.AgeText(TimeSpan.FromMinutes(12.5)));
        Assert.Equal("0 分鐘", PlantSummaries.AgeText(TimeSpan.FromMinutes(-1)));
    }

    string PathOf => PlantSummaryStore.PathBeside(Path.Combine(_tmp.Path, "範例廠.json"));

    [Fact]
    public void 存在廠別設定檔旁_存檔後讀回內容相同()
    {
        Assert.EndsWith("範例廠.summary.json", PathOf);
        Assert.Equal((null, null), PlantSummaryStore.Load(PathOf));

        var f = PlantSummaries.Approve(PlantSummaries.SaveNote(PlantSummaries.Generate(null, "範例廠", Sample(), "甲", T0), "虛構說明"), "乙", T0.AddHours(1));
        var stamp = PlantSummaryStore.Save(PathOf, f, null);

        var (back, backStamp) = PlantSummaryStore.Load(PathOf);
        Assert.Equal(stamp, backStamp);
        Assert.Equal("範例廠", back!.Plant);
        Assert.Equal("虛構說明", back.Reviewed!.Note);
        Assert.Equal(f.Reviewed!.ByAlarmCode.Select(i => (i.Key, i.Count)), back.Reviewed.ByAlarmCode.Select(i => (i.Key, i.Count)));
        Assert.Equal(f.Reviewed.Covered, back.Reviewed.Covered);
        Assert.Contains("\"formatVersion\": 1", File.ReadAllText(PathOf));
        Assert.False(File.Exists(PathOf + ".tmp"));
    }

    [Fact]
    public void 讀取後被別人修改時不覆寫_格式較新或內容有誤時說明原因()
    {
        var f = PlantSummaries.Generate(null, "範例廠", Sample(), "甲", T0);
        var stamp = PlantSummaryStore.Save(PathOf, f, null);
        PlantSummaryStore.Save(PathOf, PlantSummaries.SaveNote(f, "別台電腦（虛構）"), stamp);

        Assert.Throws<PlantSummaryConflictException>(() => PlantSummaryStore.Save(PathOf, PlantSummaries.SaveNote(f, "這台"), stamp));
        Assert.Throws<PlantSummaryConflictException>(() => PlantSummaryStore.Save(PathOf, f, null)); // 已存在時不能當新檔

        File.WriteAllText(PathOf, "{\"formatVersion\": 9}");
        Assert.Contains("較新", Assert.Throws<FormatException>(() => PlantSummaryStore.Load(PathOf)).Message);
        File.WriteAllText(PathOf, "{ 不是 JSON");
        Assert.Throws<FormatException>(() => PlantSummaryStore.Load(PathOf));
    }
}
