using LogTrace.Engine.Incidents;
using LogTrace.Tests.Support;

namespace LogTrace.Engine.Tests;

// 異常紀錄（#110，ADR-0016）：流程、版本、審核檢查、待審核清單、存檔與衝突。所有內容都是虛構的。
public sealed class IncidentRecordTests : IDisposable
{
    readonly TempFolder _tmp = new();

    public void Dispose() => _tmp.Dispose();

    static readonly DateTimeOffset T0 = new(2030, 1, 11, 9, 0, 0, TimeSpan.FromHours(8));

    static readonly IncidentEvent Ev = new(12, "PLC/20300111/PLC-2030-01-11/PLC.txt#40", "2030-01-11 08:15:00.000", "PLC", "警報發生 SSCA01 E101（虛構）");

    static IncidentContent Complete(string cause = "行走馬達過載（虛構）") => new()
    {
        Symptom = "搬送機停在站點前（虛構）",
        Plant = "範例廠",
        CaseName = "虛構案件",
        Events = [Ev],
        AlarmCodes = ["E101"],
        Devices = ["SSCA01"],
        Downtime = "約 45 分鐘",
        Handler = "工程師甲",
        Cause = cause,
        Disposal = "停止搬送機後檢查行走馬達（虛構）",
        Executor = "工程師",
        Result = "復歸後正常（虛構）",
    };

    static IncidentRecord Reviewed() =>
        IncidentWorkflow.Approve(IncidentWorkflow.Create(IncidentSource.Case, Complete(), "甲", T0, "IR-1"), "乙", T0.AddHours(1));

    [Fact]
    public void 建立草稿_列在待審核清單_查詢不列出()
    {
        var r = IncidentWorkflow.Create(IncidentSource.Case, Complete(), "甲", T0, "IR-1");

        Assert.Equal(IncidentStatus.Draft, r.Status);
        Assert.Null(IncidentWorkflow.Effective(r));
        Assert.Empty(IncidentWorkflow.Search([r], ""));
        var item = Assert.Single(ReviewQueue.Build([r], "乙", T0.AddHours(3)));
        Assert.Equal("新草稿", item.Kind);
        Assert.Equal(TimeSpan.FromHours(3), item.Waiting);
    }

    [Fact]
    public void 時間編號_含建立時間()
    {
        Assert.Matches(@"^IR-20300111-090000-[0-9a-f]{4}$", IncidentWorkflow.NewId(T0));
    }

    [Fact]
    public void 審核通過成為第一版_離開待審核清單_查詢列出()
    {
        var r = Reviewed();

        Assert.Equal(IncidentStatus.Reviewed, r.Status);
        var v = Assert.Single(r.Versions);
        Assert.Equal((1, "乙"), (v.Rev, v.Reviewer));
        Assert.Empty(v.Changes);
        Assert.Null(r.Work);
        Assert.Empty(ReviewQueue.Build([r], "乙", T0));
        Assert.Single(IncidentWorkflow.Search([r], "馬達 SSCA01"));
        Assert.Empty(IncidentWorkflow.Search([r], "電梯"));
    }

    [Fact]
    public void 擋下的條件_AI推測_必填空白_從案件建立沒有事件()
    {
        var content = Complete() with { Cause = "", Events = [], AiGuess = [IncidentFields.Disposal] };
        var r = IncidentWorkflow.Create(IncidentSource.Case, content, "甲", T0);

        var check = IncidentWorkflow.Check(r, "乙");

        Assert.False(check.CanApprove);
        Assert.Contains("「處置」仍是 AI 推測", check.Blocks);
        Assert.Contains("「原因」空白", check.Blocks);
        Assert.Contains("從案件建立的紀錄沒有相關事件", check.Blocks);
        Assert.Throws<InvalidOperationException>(() => IncidentWorkflow.Approve(r, "乙", T0));
        Assert.Contains("沒有填審核者", IncidentWorkflow.Check(r, " ").Blocks);
    }

    [Fact]
    public void 只提醒的條件_執行者空白_維修紀錄沒有事件_同一人()
    {
        var r = IncidentWorkflow.Create(IncidentSource.Maintenance, Complete() with { Events = [], Executor = "" }, "甲", T0);

        var check = IncidentWorkflow.Check(r, "甲");

        Assert.True(check.CanApprove);
        Assert.Equal(["「執行者」空白", "維修紀錄沒有事件序號", "撰寫者與審核者同一人"], check.Warnings);
        var approved = IncidentWorkflow.Approve(r, "甲", T0);
        Assert.Equal(check.Warnings, approved.Versions[0].Warnings); // 審核時的提醒保存在該版
    }

    [Fact]
    public void 修訂中舊版繼續有效_審核通過成為第二版並記錄改過的欄位()
    {
        var revising = IncidentWorkflow.Revise(Reviewed(), "甲", T0.AddDays(1));
        revising = IncidentWorkflow.SaveWork(revising, revising.Work! with { Cause = "減速機異常（虛構）" }, "甲", T0.AddDays(1));

        Assert.Equal(IncidentStatus.Revising, revising.Status);
        Assert.Equal("行走馬達過載（虛構）", IncidentWorkflow.Effective(revising)!.Content.Cause); // 查詢仍是舊版
        Assert.Single(IncidentWorkflow.Search([revising], "過載"));
        Assert.Equal([new FieldChange("原因", "行走馬達過載（虛構）", "減速機異常（虛構）")], IncidentWorkflow.PendingChanges(revising));
        Assert.Equal("修訂中", Assert.Single(ReviewQueue.Build([revising], "乙", T0.AddDays(2))).Kind);

        var v2 = IncidentWorkflow.Approve(revising, "乙", T0.AddDays(2));

        Assert.Equal(2, v2.Versions.Count);
        Assert.Equal([new FieldChange("原因", "行走馬達過載（虛構）", "減速機異常（虛構）")], v2.Versions[1].Changes);
        Assert.Equal("減速機異常（虛構）", IncidentWorkflow.Effective(v2)!.Content.Cause);
        Assert.Equal("行走馬達過載（虛構）", v2.Versions[0].Content.Cause); // 舊版完整保存
    }

    [Fact]
    public void 修訂沒有修改_提醒()
    {
        var revising = IncidentWorkflow.Revise(Reviewed(), "甲", T0);

        Assert.Contains("和上一版相比沒有修改", IncidentWorkflow.Check(revising, "乙").Warnings);
    }

    [Fact]
    public void AI推測的標記改變也算改過()
    {
        var a = Complete() with { AiGuess = [IncidentFields.Cause] };

        var change = Assert.Single(IncidentFields.Diff(a, Complete()));
        Assert.Equal(("原因", "［AI 推測］行走馬達過載（虛構）"), (change.Field, change.Old));
    }

    [Fact]
    public void 放棄修改_回到已審核_修改不保留()
    {
        var revising = IncidentWorkflow.Revise(Reviewed(), "甲", T0);
        revising = IncidentWorkflow.SaveWork(revising, revising.Work! with { Cause = "改過" }, "甲", T0);

        var r = IncidentWorkflow.CancelRevise(revising, "甲", T0);

        Assert.Equal(IncidentStatus.Reviewed, r.Status);
        Assert.Null(r.Work);
        Assert.Single(r.Versions);
    }

    [Fact]
    public void 停用不刪除_恢復退回草稿_重新審核成為新的一版()
    {
        var disabled = IncidentWorkflow.Disable(Reviewed(), "原因判斷錯誤（虛構）", "乙", T0.AddDays(1));

        Assert.Equal(IncidentStatus.Disabled, disabled.Status);
        Assert.Single(disabled.Versions);
        Assert.Empty(IncidentWorkflow.Search([disabled], ""));
        Assert.Empty(ReviewQueue.Build([disabled], "乙", T0));
        Assert.Throws<InvalidOperationException>(() => IncidentWorkflow.Disable(Reviewed(), " ", "乙", T0));

        var restored = IncidentWorkflow.Restore(disabled, "乙", T0.AddDays(2));
        Assert.Equal(IncidentStatus.Draft, restored.Status);
        Assert.Empty(IncidentWorkflow.Search([restored], "")); // 審核通過前查詢仍沒有這筆
        Assert.Equal("恢復", Assert.Single(ReviewQueue.Build([restored], "乙", T0.AddDays(2))).Kind);

        var again = IncidentWorkflow.Approve(IncidentWorkflow.SaveWork(restored, restored.Work! with { Cause = "減速機異常（虛構）" }, "乙", T0), "丙", T0.AddDays(3));
        Assert.Equal(2, again.Versions[^1].Rev);
        Assert.Single(IncidentWorkflow.Search([again], "減速機"));
        Assert.Contains(again.History, h => h.Action == "停用" && h.Note == "原因判斷錯誤（虛構）");
    }

    [Fact]
    public void 未審核的草稿也可以停用_恢復後草稿內容保留()
    {
        var draft = IncidentWorkflow.Create(IncidentSource.Case, Complete() with { Cause = "草稿中的原因" }, "甲", T0);

        var restored = IncidentWorkflow.Restore(IncidentWorkflow.Disable(draft, "不需要整理（虛構）", "甲", T0), "甲", T0);

        Assert.Equal("草稿中的原因", restored.Work!.Cause);
        Assert.Equal("新草稿", IncidentWorkflow.QueueKind(restored));
    }

    [Fact]
    public void 待審核清單_可以審核的排前面_同類依等待時間由久到新()
    {
        var blockedOld = IncidentWorkflow.Create(IncidentSource.Case, Complete() with { Result = "" }, "甲", T0, "IR-A");
        var readyNew = IncidentWorkflow.Create(IncidentSource.Case, Complete(), "甲", T0.AddHours(5), "IR-B");
        var readyOld = IncidentWorkflow.Create(IncidentSource.Maintenance, Complete(), "甲", T0.AddHours(1), "IR-C");
        var blockedNew = IncidentWorkflow.Create(IncidentSource.Case, Complete() with { AiGuess = [IncidentFields.Symptom] }, "甲", T0.AddHours(2), "IR-D");

        var queue = ReviewQueue.Build([blockedOld, readyNew, readyOld, blockedNew, Reviewed()], "乙", T0.AddDays(1));

        Assert.Equal(["IR-C", "IR-B", "IR-A", "IR-D"], queue.Select(q => q.Record.Id));
        Assert.Equal(["維修紀錄", "新草稿", "新草稿", "新草稿"], queue.Select(q => q.Kind));
    }

    [Theory]
    [InlineData(5, "5 分鐘")]
    [InlineData(150, "2 小時")]
    [InlineData(60 * 50, "2 天 2 小時")]
    public void 等待時間的文字(int minutes, string expected) =>
        Assert.Equal(expected, ReviewQueue.WaitingText(TimeSpan.FromMinutes(minutes)));

    // ---- 存放 ----

    IncidentStore Store => new(Path.Combine(_tmp.Path, "範例廠.incidents"));

    [Fact]
    public void 資料夾在廠別設定檔旁邊()
    {
        Assert.Equal(Path.Combine("D:\\Plants", "範例廠.incidents"), IncidentStore.FolderBeside(Path.Combine("D:\\Plants", "範例廠.json")));
    }

    [Fact]
    public void 存檔後讀回內容相同_資料夾不存在時是空的()
    {
        Assert.Empty(Store.LoadAll().Records);
        var r = IncidentWorkflow.Revise(Reviewed(), "甲", T0);

        Store.Save(r, null);
        var (records, issues) = Store.LoadAll();

        Assert.Empty(issues);
        var loaded = Assert.Single(records).Record;
        Assert.Equal(IncidentStore.Serialize(r), IncidentStore.Serialize(loaded));
        Assert.Contains("\"status\": \"revising\"", File.ReadAllText(Store.PathOf("IR-1")));
        Assert.Contains("搬送機停在站點前", File.ReadAllText(Store.PathOf("IR-1"))); // 中文直接寫出
    }

    [Fact]
    public void 讀取後檔案被別人修改_存檔時不覆寫()
    {
        var saved = Store.Save(Reviewed(), null);
        var other = Store.LoadAll().Records[0];
        Store.Save(IncidentWorkflow.Revise(other.Record, "另一台電腦", T0), other.Stamp); // 另一台電腦先存

        var ex = Assert.Throws<IncidentConflictException>(() => Store.Save(IncidentWorkflow.Disable(saved.Record, "理由", "甲", T0), saved.Stamp));

        Assert.Equal("IR-1", ex.Id);
        Assert.Equal(IncidentStatus.Revising, Store.LoadAll().Records[0].Record.Status);
        Assert.Throws<IncidentConflictException>(() => Store.Save(Reviewed(), null)); // 新紀錄撞到已有的檔案
    }

    [Fact]
    public void 讀不了的檔案不使用_列出檔名與原因()
    {
        Store.Save(Reviewed(), null);
        File.WriteAllText(Path.Combine(Store.Folder, "壞掉.json"), "{ 不是 JSON");
        File.WriteAllText(Path.Combine(Store.Folder, "IR-9.json"), "{\"formatVersion\": 99, \"id\": \"IR-9\"}");
        File.Copy(Store.PathOf("IR-1"), Path.Combine(Store.Folder, "IR-2.json"));

        var (records, issues) = Store.LoadAll();

        Assert.Single(records);
        Assert.Equal(3, issues.Count);
        Assert.Contains(issues, i => i.FileName == "IR-9.json" && i.Reason.Contains("較新"));
        Assert.Contains(issues, i => i.FileName == "壞掉.json" && i.Reason.Contains("JSON"));
        Assert.Contains(issues, i => i.FileName == "IR-2.json" && i.Reason.Contains("不一致"));
    }

    [Fact]
    public void repo內的虛構範例可以讀取()
    {
        var (records, issues) = new IncidentStore(Path.Combine(TempFolder.Fixture("playback-prototype"), "plant.incidents")).LoadAll();

        Assert.Empty(issues);
        Assert.NotEmpty(records);
    }
}
