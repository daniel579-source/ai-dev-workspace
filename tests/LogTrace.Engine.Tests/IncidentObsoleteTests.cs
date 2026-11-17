using LogTrace.Engine.Incidents;
using LogTrace.Tests.Support;

namespace LogTrace.Engine.Tests;

// 異常紀錄的「已過時」狀態（#167，ADR-0023 第 2 點）。所有內容都是虛構的。
public sealed class IncidentObsoleteTests : IDisposable
{
    readonly TempFolder _tmp = new();

    public void Dispose() => _tmp.Dispose();

    static readonly DateTimeOffset T0 = new(2030, 1, 11, 9, 0, 0, TimeSpan.FromHours(8));

    static IncidentContent Content(string symptom = "搬送機停在站點前（虛構）") => new()
    {
        Symptom = symptom,
        Plant = "範例廠",
        Events = [new(12, "PLC/x.txt#40", "2030-01-11 08:15:00.000", "PLC", "警報發生（虛構）")],
        Cause = "行走馬達過載（虛構）",
        Disposal = "更換行走馬達（虛構）",
        Executor = "工程師",
        Result = "復歸後正常（虛構）",
    };

    static IncidentRecord Reviewed(string id = "IR-1", string symptom = "搬送機停在站點前（虛構）") =>
        IncidentWorkflow.Approve(IncidentWorkflow.Create(IncidentSource.Case, Content(symptom), "甲", T0, id), "乙", T0.AddHours(1));

    IncidentStore Store => new(Path.Combine(_tmp.Path, "範例廠.incidents"));

    [Fact]
    public void 已審核可以標示已過時_理由可以不填_查詢預設不列出_記入經過()
    {
        var r = IncidentWorkflow.MarkObsolete(Reviewed(), "  ", "丙", T0.AddDays(30));

        Assert.Equal(IncidentStatus.Obsolete, r.Status);
        Assert.Null(r.ObsoleteReason);
        Assert.Null(IncidentWorkflow.Effective(r));
        Assert.Empty(IncidentWorkflow.Search([r], ""));
        Assert.Null(IncidentWorkflow.QueueKind(r));
        Assert.Equal(("丙", "標示已過時", ""), (r.History[^1].User, r.History[^1].Action, r.History[^1].Note));
    }

    [Fact]
    public void 修訂中標示已過時_尚未審核的修改不保留_理由記錄()
    {
        var revising = IncidentWorkflow.SaveWork(IncidentWorkflow.Revise(Reviewed(), "甲", T0.AddDays(1)),
            Content() with { Cause = "減速機異常（虛構）" }, "甲", T0.AddDays(1));

        var r = IncidentWorkflow.MarkObsolete(revising, "設備已更換（虛構）", "甲", T0.AddDays(2));

        Assert.Equal(IncidentStatus.Obsolete, r.Status);
        Assert.Null(r.Work);
        Assert.Equal("設備已更換（虛構）", r.ObsoleteReason);
        Assert.Equal("行走馬達過載（虛構）", r.Versions[^1].Content.Cause);
    }

    [Fact]
    public void 草稿停用或已過時不能標示()
    {
        var draft = IncidentWorkflow.Create(IncidentSource.Case, Content(), "甲", T0, "IR-1");
        var disabled = IncidentWorkflow.Disable(Reviewed(), "內容有誤（虛構）", "甲", T0.AddDays(1));
        var obsolete = IncidentWorkflow.MarkObsolete(Reviewed(), null, "甲", T0.AddDays(1));

        Assert.Throws<InvalidOperationException>(() => IncidentWorkflow.MarkObsolete(draft, null, "甲", T0));
        Assert.Throws<InvalidOperationException>(() => IncidentWorkflow.MarkObsolete(disabled, null, "甲", T0));
        Assert.Throws<InvalidOperationException>(() => IncidentWorkflow.MarkObsolete(obsolete, null, "甲", T0));
    }

    [Fact]
    public void 勾選顯示已過時時以最後一版比對()
    {
        var obsolete = IncidentWorkflow.MarkObsolete(Reviewed("IR-1", "條碼讀不到（虛構）"), null, "甲", T0.AddDays(1));
        var active = Reviewed("IR-2", "搬送機停止（虛構）");

        Assert.Equal(["IR-2"], IncidentWorkflow.Search([obsolete, active], "").Select(r => r.Id));
        Assert.Equal(["IR-1", "IR-2"], IncidentWorkflow.Search([obsolete, active], "", includeObsolete: true).Select(r => r.Id));
        Assert.Equal(["IR-1"], IncidentWorkflow.Search([obsolete, active], "條碼", includeObsolete: true).Select(r => r.Id));
        Assert.Equal(1, IncidentWorkflow.Searchable(obsolete, true)!.Rev);
        Assert.Null(IncidentWorkflow.Searchable(obsolete, false));
    }

    [Fact]
    public void 恢復退回草稿_重新審核成為新的一版()
    {
        var obsolete = IncidentWorkflow.MarkObsolete(Reviewed(), "設備已更換（虛構）", "甲", T0.AddDays(1));

        var restored = IncidentWorkflow.Restore(obsolete, "甲", T0.AddDays(2));

        Assert.Equal(IncidentStatus.Draft, restored.Status);
        Assert.Null(restored.ObsoleteReason);
        Assert.Equal("恢復", IncidentWorkflow.QueueKind(restored));
        Assert.Equal(obsolete.Versions[^1].Content, restored.Work);
        var again = IncidentWorkflow.Approve(IncidentWorkflow.SaveWork(restored, Content() with { Disposal = "改用新型號馬達（虛構）" }, "甲", T0.AddDays(2)), "乙", T0.AddDays(3));
        Assert.Equal(2, again.Versions[^1].Rev);
    }

    [Fact]
    public void 已過時的紀錄可以停用_停用後不保留已過時理由()
    {
        var obsolete = IncidentWorkflow.MarkObsolete(Reviewed(), "設備已更換（虛構）", "甲", T0.AddDays(1));

        var disabled = IncidentWorkflow.Disable(obsolete, "其實內容有誤（虛構）", "甲", T0.AddDays(2));

        Assert.Equal(IncidentStatus.Disabled, disabled.Status);
        Assert.Null(disabled.ObsoleteReason);
    }

    [Fact]
    public void 已過時寫成格式版本2_其他維持1_恢復後回到1()
    {
        var obsolete = IncidentWorkflow.MarkObsolete(Reviewed(), "設備已更換（虛構）", "甲", T0.AddDays(1));
        Store.Save(obsolete, null);

        var text = File.ReadAllText(Store.PathOf("IR-1"));
        Assert.Contains("\"formatVersion\": 2", text);
        Assert.Contains("\"status\": \"obsolete\"", text);
        Assert.Contains("\"obsoleteReason\": \"設備已更換（虛構）\"", text);
        var back = Store.LoadAll().Records.Single().Record;
        Assert.Equal(IncidentStatus.Obsolete, back.Status);
        Assert.Equal("設備已更換（虛構）", back.ObsoleteReason);

        var stamp = Store.LoadAll().Records.Single().Stamp;
        Store.Save(IncidentWorkflow.Restore(back, "甲", T0.AddDays(2)), stamp);
        var restored = File.ReadAllText(Store.PathOf("IR-1"));
        Assert.Contains("\"formatVersion\": 1", restored);
        Assert.DoesNotContain("obsoleteReason", restored);

        Assert.Contains("\"formatVersion\": 1", IncidentStore.Serialize(Reviewed()));
    }

    [Fact]
    public void 舊版程式只能讀到版本1_讀到版本2時提示更新()
    {
        // 舊版程式的判斷：格式版本大於它能讀的版本時列為「較新」（以版本 3 模擬本程式之後的版本）
        Directory.CreateDirectory(Store.Folder);
        File.WriteAllText(Path.Combine(Store.Folder, "IR-9.json"), "{\"formatVersion\": 3, \"id\": \"IR-9\"}");

        var issue = Assert.Single(Store.LoadAll().Issues);
        Assert.Contains("格式版本 3 較新，請更新程式", issue.Reason);
        Assert.Equal(2, IncidentRecord.CurrentFormatVersion);
    }
}
