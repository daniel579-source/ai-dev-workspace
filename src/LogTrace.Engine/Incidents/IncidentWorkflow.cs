using LogTrace.Engine.Search;

namespace LogTrace.Engine.Incidents;

/// <summary>欄位的鍵、名稱與顯示文字（比較修改、標示改過的欄位、搜尋都以顯示文字為準）。</summary>
public static class IncidentFields
{
    public const string Symptom = "symptom", Occurred = "occurred", Plant = "plant", CaseName = "caseName", Events = "events",
        AlarmCodes = "alarmCodes", Devices = "devices", Downtime = "downtime", Handler = "handler", Cause = "cause",
        Disposal = "disposal", Executor = "executor", Result = "result";

    /// <summary>可以標示「AI 推測」的欄位。</summary>
    public static IReadOnlyList<string> AiGuessable { get; } = [Symptom, Cause, Disposal];

    public static IReadOnlyList<(string Key, string Label, Func<IncidentContent, string> Text)> All { get; } =
    [
        (Symptom, "現象", c => c.Symptom),
        (Occurred, "發生時間", c => c.OccurredFrom.Length == 0 && c.OccurredTo.Length == 0 ? "" : $"{c.OccurredFrom} ～ {c.OccurredTo}"),
        (Plant, "廠別", c => c.Plant),
        (CaseName, "案件", c => c.CaseName),
        (Events, "相關事件", c => string.Join("\n", c.Events.Select(EventText))),
        (AlarmCodes, "警報代碼", c => string.Join("、", c.AlarmCodes)),
        (Devices, "相關設備", c => string.Join("、", c.Devices)),
        (Downtime, "停機時間", c => c.Downtime),
        (Handler, "處理人", c => c.Handler),
        (Cause, "原因", c => c.Cause),
        (Disposal, "處置", c => c.Disposal),
        (Executor, "執行者", c => c.Executor),
        (Result, "結果", c => c.Result),
    ];

    public static string Label(string key) => All.FirstOrDefault(f => f.Key == key).Label ?? key;

    public static string Text(IncidentContent content, string key) => All.First(f => f.Key == key).Text(content);

    public static string EventText(IncidentEvent e) => $"#{e.Sequence} {e.Time} {e.Module} {e.Message}";

    /// <summary>b 相對於 a 改過的欄位（依欄位順序）；AI 推測的標記改變也算該欄位改過。</summary>
    public static IReadOnlyList<FieldChange> Diff(IncidentContent a, IncidentContent b) =>
        All.Select(f => (f.Label, Old: Marked(a, f.Key, f.Text), New: Marked(b, f.Key, f.Text)))
            .Where(x => x.Old != x.New).Select(x => new FieldChange(x.Label, x.Old, x.New)).ToList();

    static string Marked(IncidentContent c, string key, Func<IncidentContent, string> text) =>
        c.AiGuess.Contains(key) ? $"［AI 推測］{text(c)}" : text(c);
}

/// <summary>審核檢查：擋下的原因（有任一項就不能審核通過）與只提醒的事項。</summary>
public sealed record ReviewCheck(IReadOnlyList<string> Blocks, IReadOnlyList<string> Warnings)
{
    public bool CanApprove => Blocks.Count == 0;
}

/// <summary>
/// 異常紀錄的流程（ADR-0016 第 3 點，2026-11-13 補充）：建立草稿、修改、審核通過（成為新的一版）、
/// 修訂（舊版繼續有效）、放棄修改、停用（不刪除）、恢復（退回草稿）。每個動作傳回新的紀錄，記入經過。
/// </summary>
public static class IncidentWorkflow
{
    /// <summary>時間編號：IR-年月日-時分秒-4 碼亂數，不同電腦同時建立也不會撞號（使用者，2026-11-13）。</summary>
    public static string NewId(DateTimeOffset now) => $"IR-{now:yyyyMMdd-HHmmss}-{Random.Shared.Next(0x10000):x4}";

    public static IncidentRecord Create(IncidentSource source, IncidentContent content, string author, DateTimeOffset now, string? id = null) => new()
    {
        Id = id ?? NewId(now),
        Source = source,
        Status = IncidentStatus.Draft,
        Author = author.Trim(),
        CreatedAt = now,
        Work = content,
        QueuedAt = now,
        History = [new(now, author.Trim(), source == IncidentSource.Maintenance ? "建立（維修紀錄）" : "建立草稿", "")],
    };

    /// <summary>目前有效的一版：已審核與修訂中時為最新一版；草稿與停用時為 null（查詢不列出）。</summary>
    public static IncidentVersion? Effective(IncidentRecord r) =>
        r.Status is IncidentStatus.Reviewed or IncidentStatus.Revising && r.Versions.Count > 0 ? r.Versions[^1] : null;

    /// <summary>可以編輯的狀態：草稿與修訂中。</summary>
    public static bool IsEditable(IncidentRecord r) => r.Status is IncidentStatus.Draft or IncidentStatus.Revising && r.Work is not null;

    /// <summary>修訂中尚未審核的變更（相對於目前有效的一版）；不在修訂中時為空。</summary>
    public static IReadOnlyList<FieldChange> PendingChanges(IncidentRecord r) =>
        r.Status == IncidentStatus.Revising && r.Work is { } w && r.Versions.Count > 0 ? IncidentFields.Diff(r.Versions[^1].Content, w) : [];

    /// <summary>待審核清單的種類：新草稿、維修紀錄、修訂中、恢復；不在清單中時為 null。</summary>
    public static string? QueueKind(IncidentRecord r) => r.Status switch
    {
        IncidentStatus.Revising => "修訂中",
        IncidentStatus.Draft when r.Versions.Count > 0 => "恢復",
        IncidentStatus.Draft when r.Source == IncidentSource.Maintenance => "維修紀錄",
        IncidentStatus.Draft => "新草稿",
        _ => null,
    };

    public static IncidentRecord SaveWork(IncidentRecord r, IncidentContent content, string user, DateTimeOffset now)
    {
        if (!IsEditable(r)) throw new InvalidOperationException("這筆紀錄目前不能修改");
        return r with { Work = content, History = [.. r.History, new(now, user.Trim(), "修改", "")] };
    }

    /// <summary>
    /// 審核檢查（雛形採用的條件，ADR-0016 第 3 點補充 9）。
    /// 擋下：沒有審核者；現象、原因、處置任一項仍是 AI 推測；現象、原因、處置、結果有空白；從案件產生的紀錄沒有相關事件。
    /// 只提醒：執行者空白；維修紀錄沒有事件序號；撰寫者與審核者同一人；和上一版相比沒有修改。
    /// </summary>
    public static ReviewCheck Check(IncidentRecord r, string? reviewer)
    {
        var blocks = new List<string>();
        var warnings = new List<string>();
        if (r.Work is not { } w || !IsEditable(r)) return new(["這筆紀錄目前不在待審核狀態"], []);

        if (string.IsNullOrWhiteSpace(reviewer)) blocks.Add("沒有填審核者");
        foreach (var key in IncidentFields.AiGuessable.Where(w.AiGuess.Contains))
            blocks.Add($"「{IncidentFields.Label(key)}」仍是 AI 推測");
        foreach (var (key, text) in new[] { (IncidentFields.Symptom, w.Symptom), (IncidentFields.Cause, w.Cause),
                     (IncidentFields.Disposal, w.Disposal), (IncidentFields.Result, w.Result) })
            if (string.IsNullOrWhiteSpace(text)) blocks.Add($"「{IncidentFields.Label(key)}」空白");
        if (r.Source == IncidentSource.Case && w.Events.Count == 0) blocks.Add("從案件建立的紀錄沒有相關事件");

        if (string.IsNullOrWhiteSpace(w.Executor)) warnings.Add("「執行者」空白");
        if (r.Source == IncidentSource.Maintenance && w.Events.Count == 0) warnings.Add("維修紀錄沒有事件序號");
        if (!string.IsNullOrWhiteSpace(reviewer) && string.Equals(reviewer.Trim(), r.Author, StringComparison.OrdinalIgnoreCase))
            warnings.Add("撰寫者與審核者同一人");
        if (r.Versions.Count > 0 && IncidentFields.Diff(r.Versions[^1].Content, w).Count == 0) warnings.Add("和上一版相比沒有修改");
        return new(blocks, warnings);
    }

    /// <summary>審核通過：成為新的一版（保存完整內容與改過的欄位），離開待審核清單。有擋下的原因時拋出例外。</summary>
    public static IncidentRecord Approve(IncidentRecord r, string reviewer, DateTimeOffset now)
    {
        var check = Check(r, reviewer);
        if (!check.CanApprove) throw new InvalidOperationException("不能審核通過：" + string.Join("；", check.Blocks));
        var work = r.Work!;
        var changes = r.Versions.Count == 0 ? [] : IncidentFields.Diff(r.Versions[^1].Content, work);
        var version = new IncidentVersion(r.Versions.Count + 1, work, changes, reviewer.Trim(), now, check.Warnings);
        return r with
        {
            Status = IncidentStatus.Reviewed,
            Work = null,
            QueuedAt = null,
            Versions = [.. r.Versions, version],
            History = [.. r.History, new(now, reviewer.Trim(), $"審核通過（第 {version.Rev} 版）", string.Join("；", check.Warnings))],
        };
    }

    /// <summary>修訂已審核的紀錄：改為修訂中，舊版繼續有效；修改從目前有效的內容開始。</summary>
    public static IncidentRecord Revise(IncidentRecord r, string user, DateTimeOffset now)
    {
        if (r.Status != IncidentStatus.Reviewed) throw new InvalidOperationException("只有已審核的紀錄可以修訂");
        return r with
        {
            Status = IncidentStatus.Revising,
            Work = r.Versions[^1].Content,
            QueuedAt = now,
            History = [.. r.History, new(now, user.Trim(), "開始修訂", "")],
        };
    }

    /// <summary>放棄修改：回到已審核，修改的內容不保留。</summary>
    public static IncidentRecord CancelRevise(IncidentRecord r, string user, DateTimeOffset now)
    {
        if (r.Status != IncidentStatus.Revising) throw new InvalidOperationException("這筆紀錄不在修訂中");
        return r with
        {
            Status = IncidentStatus.Reviewed,
            Work = null,
            QueuedAt = null,
            History = [.. r.History, new(now, user.Trim(), "放棄修改", "")],
        };
    }

    /// <summary>
    /// 停用（附理由，不刪除）：查詢不再列出，離開待審核清單。
    /// 從未審核過的草稿也可以停用，草稿內容保留，恢復時繼續使用；修訂中停用時，尚未審核的修改不保留。
    /// </summary>
    public static IncidentRecord Disable(IncidentRecord r, string reason, string user, DateTimeOffset now)
    {
        if (r.Status == IncidentStatus.Disabled) throw new InvalidOperationException("這筆紀錄已經停用");
        if (string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("請填寫停用的理由");
        return r with
        {
            Status = IncidentStatus.Disabled,
            Work = r.Versions.Count == 0 ? r.Work : null,
            QueuedAt = null,
            DisabledReason = reason.Trim(),
            History = [.. r.History, new(now, user.Trim(), "停用", reason.Trim())],
        };
    }

    /// <summary>恢復：退回草稿重新審核；審核通過前查詢中仍然沒有這筆（ADR-0016 第 3 點補充 5）。</summary>
    public static IncidentRecord Restore(IncidentRecord r, string user, DateTimeOffset now)
    {
        if (r.Status != IncidentStatus.Disabled) throw new InvalidOperationException("只有停用的紀錄可以恢復");
        return r with
        {
            Status = IncidentStatus.Draft,
            Work = r.Work ?? r.Versions[^1].Content,
            QueuedAt = now,
            DisabledReason = null,
            History = [.. r.History, new(now, user.Trim(), "恢復（退回草稿）", "")],
        };
    }

    /// <summary>
    /// 以空白分隔的關鍵字搜尋目前有效的版本（所有欄位，部分比對、每個關鍵字都要包含，ADR-0010 的正規化）。
    /// 草稿與停用不列出（ADR-0016 第 6 點補充）。沒有關鍵字時傳回全部有效的紀錄。
    /// </summary>
    public static IReadOnlyList<IncidentRecord> Search(IEnumerable<IncidentRecord> records, string keywords)
    {
        var terms = keywords.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(TextNormalizer.Normalize).ToList();
        return records.Where(r => Effective(r) is { } v && Matches(v.Content, terms)).ToList();
    }

    static bool Matches(IncidentContent c, List<string> terms)
    {
        if (terms.Count == 0) return true;
        var text = TextNormalizer.Normalize(string.Join("\n", IncidentFields.All.Select(f => f.Text(c))));
        return terms.All(t => text.Contains(t, StringComparison.Ordinal));
    }
}

/// <summary>待審核清單的一項。</summary>
public sealed record ReviewQueueItem(IncidentRecord Record, string Kind, TimeSpan Waiting, ReviewCheck Check);

/// <summary>
/// 待審核清單（ADR-0016 第 3 點補充 8）：草稿與修訂中，包含還不能審核的；
/// 可以審核的排前面，同類依等待時間由久到新。
/// </summary>
public static class ReviewQueue
{
    public static IReadOnlyList<ReviewQueueItem> Build(IEnumerable<IncidentRecord> records, string? reviewer, DateTimeOffset now) =>
        records.Where(r => IncidentWorkflow.QueueKind(r) is not null)
            .Select(r => new ReviewQueueItem(r, IncidentWorkflow.QueueKind(r)!, now - (r.QueuedAt ?? r.CreatedAt), IncidentWorkflow.Check(r, reviewer)))
            .OrderBy(i => i.Check.CanApprove ? 0 : 1)
            .ThenByDescending(i => i.Waiting)
            .ThenBy(i => i.Record.Id, StringComparer.Ordinal)
            .ToList();

    /// <summary>等待時間的文字：未滿 1 小時以分鐘、未滿 1 天以小時、其餘以天與小時表示。</summary>
    public static string WaitingText(TimeSpan t) =>
        t < TimeSpan.Zero ? "0 分鐘"
        : t.TotalHours < 1 ? $"{(int)t.TotalMinutes} 分鐘"
        : t.TotalDays < 1 ? $"{(int)t.TotalHours} 小時"
        : $"{(int)t.TotalDays} 天 {t.Hours} 小時";
}
