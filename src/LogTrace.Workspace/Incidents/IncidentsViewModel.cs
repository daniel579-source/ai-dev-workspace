using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogTrace.Engine.Incidents;

namespace LogTrace.Workspace.Incidents;

/// <summary>查詢結果的一列：目前有效的版本；最新一版改過的欄位顯眼標示（ADR-0016 第 3 點補充 3）。</summary>
public sealed record IncidentRowViewModel(string Id, string Symptom, string Status, int Rev, string Changed, string Reviewed);

/// <summary>待審核清單的一列：種類、等待時間、擋下的原因與提醒。</summary>
public sealed record IncidentQueueRowViewModel(string Id, string Kind, string Waiting, string Symptom, string Blocks, string Warnings, bool CanApprove);

/// <summary>詳細資訊的一行；Changed＝最新一版改過的欄位（★），Pending＝修訂中尚未審核的變更（◆），畫面上醒目標示。</summary>
public sealed record IncidentDetailLine(string Text, bool Changed, bool Pending);

/// <summary>主畫面提供的案件資訊：目前選取的事件（遮罩後）與該事件的警報代碼、設備編號；跳到事件。</summary>
public sealed record IncidentEventPick(IncidentEvent Event, IReadOnlyList<string> AlarmCodes, IReadOnlyList<string> Devices);

/// <summary>
/// 「異常紀錄」面板（ADR-0016，#110）：查詢（只列目前有效的版本）、待審核清單、詳細資訊與修訂經過、
/// 建立與編輯草稿、審核、修訂、放棄修改、停用、恢復。不使用 LLM。
/// 紀錄放在廠別設定檔旁邊的 &lt;廠別設定檔名&gt;.incidents\，每筆一個 JSON 檔。
/// </summary>
public sealed partial class IncidentsViewModel : ObservableObject
{
    static readonly char[] ListSeparators = [',', '，', '、', ';', '；', '\n', '\r', '\t', ' '];

    readonly Func<string, string, bool> _confirm;
    IncidentStore? _store;
    string _plant = "";
    string? _caseName;
    List<StoredIncident> _records = [];

    /// <param name="confirm">確認對話框（標題、訊息）；確定時傳回 true。</param>
    public IncidentsViewModel(Func<string, string, bool> confirm) => _confirm = confirm;

    /// <summary>目前時間（測試時可以替換）。</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.Now;

    /// <summary>目前選取的事件；沒有選取時傳回 null。</summary>
    public Func<IncidentEventPick?> PickEvent { get; set; } = () => null;

    /// <summary>以穩定識別碼跳到目前案件中的事件；找不到時傳回 false。</summary>
    public Func<string, bool> GoToStableId { get; set; } = _ => false;

    /// <summary>使用者名稱：建立時的撰寫者、審核時的審核者；預設為 Windows 使用者名稱，可以修改（先不限制誰可以審核）。</summary>
    [ObservableProperty]
    public partial string UserName { get; set; } = Environment.UserName;

    [ObservableProperty]
    public partial string Status { get; private set; } = "請開啟案件。";

    /// <summary>讀不了的檔案：「檔名：原因」。</summary>
    public ObservableCollection<string> Issues { get; } = [];

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    public ObservableCollection<IncidentRowViewModel> Results { get; } = [];

    public ObservableCollection<IncidentQueueRowViewModel> Queue { get; } = [];

    /// <summary>停用的紀錄（可以恢復）。</summary>
    public ObservableCollection<IncidentRowViewModel> Disabled { get; } = [];

    [ObservableProperty]
    public partial IncidentRowViewModel? SelectedResult { get; set; }

    [ObservableProperty]
    public partial IncidentQueueRowViewModel? SelectedQueueItem { get; set; }

    [ObservableProperty]
    public partial IncidentRowViewModel? SelectedDisabled { get; set; }

    partial void OnSelectedResultChanged(IncidentRowViewModel? value) { if (value is not null) Select(value.Id); }
    partial void OnSelectedQueueItemChanged(IncidentQueueRowViewModel? value) { if (value is not null) Select(value.Id); }
    partial void OnSelectedDisabledChanged(IncidentRowViewModel? value) { if (value is not null) Select(value.Id); }

    /// <summary>目前選取的紀錄編號。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(ApproveCommand), nameof(ReviseCommand), nameof(CancelReviseCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisableCommand), nameof(RestoreCommand), nameof(AddSelectedEventCommand))]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    public partial string? SelectedId { get; private set; }

    StoredIncident? Current => _records.FirstOrDefault(r => r.Record.Id == SelectedId);

    /// <summary>選取的紀錄可以編輯（草稿或修訂中）。</summary>
    public bool IsEditing => Current is { } c && IncidentWorkflow.IsEditable(c.Record);

    /// <summary>詳細資訊：目前有效的版本（最新一版改過的欄位標示 ★）、修訂中尚未審核的變更、停用理由。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailLines))]
    public partial string Detail { get; private set; } = "";

    /// <summary>詳細資訊分行，供畫面醒目標示改過的欄位與尚未審核的變更。</summary>
    public IReadOnlyList<IncidentDetailLine> DetailLines =>
        Detail.Length == 0 ? [] : Detail.Split('\n').Select(l => new IncidentDetailLine(l, l.StartsWith('★'), l.StartsWith('◆'))).ToList();

    /// <summary>修訂經過：每一版的審核者、日期與每個欄位「舊 → 新」，以及所有動作。</summary>
    [ObservableProperty]
    public partial string HistoryText { get; private set; } = "";

    /// <summary>目前審核者的審核檢查結果（擋下的原因與提醒）。</summary>
    [ObservableProperty]
    public partial string CheckText { get; private set; } = "";

    // ---- 編輯中的內容 ----

    [ObservableProperty] public partial string EditSymptom { get; set; } = "";
    [ObservableProperty] public partial bool AiSymptom { get; set; }
    [ObservableProperty] public partial string EditFrom { get; set; } = "";
    [ObservableProperty] public partial string EditTo { get; set; } = "";
    [ObservableProperty] public partial string EditPlant { get; set; } = "";
    [ObservableProperty] public partial string EditCaseName { get; set; } = "";
    [ObservableProperty] public partial string EditAlarmCodes { get; set; } = "";
    [ObservableProperty] public partial string EditDevices { get; set; } = "";
    [ObservableProperty] public partial string EditDowntime { get; set; } = "";
    [ObservableProperty] public partial string EditHandler { get; set; } = "";
    [ObservableProperty] public partial string EditCause { get; set; } = "";
    [ObservableProperty] public partial bool AiCause { get; set; }
    [ObservableProperty] public partial string EditDisposal { get; set; } = "";
    [ObservableProperty] public partial bool AiDisposal { get; set; }
    [ObservableProperty] public partial string EditExecutor { get; set; } = "";
    [ObservableProperty] public partial string EditResult { get; set; } = "";

    /// <summary>紀錄的相關事件：編輯中為草稿的事件，否則為目前有效版本的事件。</summary>
    public ObservableCollection<IncidentEvent> RecordEvents { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveEventCommand), nameof(GoToEventCommand))]
    public partial IncidentEvent? SelectedRecordEvent { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DisableCommand))]
    public partial string DisableReason { get; set; } = "";

    // ---- 開啟與重新載入 ----

    /// <summary>開啟案件時：依廠別設定檔的位置讀取異常紀錄。plantConfigPath 為 null 時（找不到設定檔）無法使用。</summary>
    public void Open(string? plantConfigPath, string plant, string caseName)
    {
        _store = plantConfigPath is null ? null : new IncidentStore(IncidentStore.FolderBeside(plantConfigPath));
        _plant = plant;
        _caseName = caseName;
        Reload();
    }

    /// <summary>關閉案件時。</summary>
    public void Close()
    {
        _store = null;
        _caseName = null;
        Query = "";
        Reload();
    }

    bool HasStore() => _store is not null;

    /// <summary>重新讀取資料夾（例如別台電腦修改後）。保留目前的選取。</summary>
    [RelayCommand]
    public void Reload()
    {
        var keep = SelectedId;
        Issues.Clear();
        _records = [];
        if (_store is not null)
        {
            try
            {
                var (records, issues) = _store.LoadAll();
                _records = records.ToList();
                foreach (var i in issues) Issues.Add($"{i.FileName}：{i.Reason}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Issues.Add($"無法讀取：{ex.Message}");
            }
        }
        RefreshLists();
        Select(_records.Any(r => r.Record.Id == keep) ? keep : null);
        Status = _store is null
            ? _caseName is null ? "請開啟案件。" : $"找不到廠別「{_plant}」的設定檔，無法使用異常紀錄。"
            : $"有效 {Results.Count:N0} 筆、待審核 {Queue.Count:N0} 筆、停用 {Disabled.Count:N0} 筆（{_store.Folder}）"
              + (Issues.Count == 0 ? "" : $"；{Issues.Count:N0} 個檔案讀不了，沒有使用");
        NotifyCommands();
    }

    [RelayCommand]
    void Search() => RefreshLists();

    void RefreshLists()
    {
        var all = _records.Select(r => r.Record).ToList();
        Results.Clear();
        foreach (var r in IncidentWorkflow.Search(all, Query)) Results.Add(Row(r));
        RefreshQueue();
        Disabled.Clear();
        foreach (var r in all.Where(r => r.Status == IncidentStatus.Disabled)) Disabled.Add(Row(r));
    }

    void RefreshQueue()
    {
        Queue.Clear();
        foreach (var i in ReviewQueue.Build(_records.Select(r => r.Record), UserName, Clock()))
            Queue.Add(new(i.Record.Id, i.Kind, ReviewQueue.WaitingText(i.Waiting), i.Record.Work?.Symptom ?? "",
                string.Join("；", i.Check.Blocks), string.Join("；", i.Check.Warnings), i.Check.CanApprove));
    }

    partial void OnUserNameChanged(string value)
    {
        RefreshQueue();
        UpdateCheck();
    }

    static string StatusLabel(IncidentStatus s) => s switch
    {
        IncidentStatus.Draft => "草稿",
        IncidentStatus.Reviewed => "已審核",
        IncidentStatus.Revising => "修訂中（舊版仍有效）",
        _ => "停用",
    };

    static IncidentRowViewModel Row(IncidentRecord r)
    {
        var v = r.Versions.Count > 0 ? r.Versions[^1] : null;
        var content = IncidentWorkflow.Effective(r)?.Content ?? r.Work ?? v?.Content;
        var changed = IncidentWorkflow.Effective(r) is { Changes.Count: > 0 } ev
            ? $"★ 第 {ev.Rev} 版改過：{string.Join("、", ev.Changes.Select(c => c.Field))}" : "";
        return new(r.Id, content?.Symptom ?? "", StatusLabel(r.Status), v?.Rev ?? 0, changed,
            v is null ? "" : $"{v.Reviewer} {v.ReviewedAt:yyyy-MM-dd}");
    }

    // ---- 選取與顯示 ----

    void Select(string? id)
    {
        SelectedId = id;
        var r = Current?.Record;
        DisableReason = "";
        LoadEditor(r is not null && IncidentWorkflow.IsEditable(r) ? r.Work! : null);
        RecordEvents.Clear();
        foreach (var e in (r is null ? null : r.Work is { } w && IncidentWorkflow.IsEditable(r) ? w : IncidentWorkflow.Effective(r)?.Content ?? r.Work ?? r.Versions.LastOrDefault()?.Content)?.Events ?? [])
            RecordEvents.Add(e);
        Detail = r is null ? "" : DetailText(r);
        HistoryText = r is null ? "" : History(r);
        UpdateCheck();
        NotifyCommands();
    }

    void UpdateCheck()
    {
        var r = Current?.Record;
        if (r is null || !IncidentWorkflow.IsEditable(r))
        {
            CheckText = "";
            return;
        }
        var check = IncidentWorkflow.Check(r with { Work = EditorContent(r.Work!) }, UserName);
        CheckText = (check.CanApprove ? "可以審核通過" : "不能審核通過：" + string.Join("；", check.Blocks))
            + (check.Warnings.Count == 0 ? "" : "\n提醒：" + string.Join("；", check.Warnings));
    }

    static string DetailText(IncidentRecord r)
    {
        var lines = new List<string> { $"{r.Id}　{StatusLabel(r.Status)}　來源：{(r.Source == IncidentSource.Case ? "案件" : "維修紀錄")}　撰寫者：{r.Author}" };
        if (r.Status == IncidentStatus.Disabled) lines.Add($"停用理由：{r.DisabledReason}");
        if (IncidentWorkflow.Effective(r) is { } v)
        {
            var changed = v.Changes.Select(c => c.Field).ToHashSet();
            lines.Add($"目前有效：第 {v.Rev} 版（審核：{v.Reviewer}，{v.ReviewedAt:yyyy-MM-dd HH:mm}）"
                + (changed.Count == 0 ? "" : "　★＝這一版改過的欄位"));
            lines.AddRange(Fields(v.Content, changed));
            if (IncidentWorkflow.PendingChanges(r) is { Count: > 0 } pending)
            {
                lines.Add("");
                lines.Add("◆ 修訂中，尚未審核的變更（審核通過前仍以上面的版本為準）：");
                lines.AddRange(pending.Select(c => $"◆ {c.Field}：{Short(c.Old)} → {Short(c.New)}"));
            }
        }
        else if (r.Work is { } w)
        {
            lines.Add(r.Versions.Count > 0 ? "尚未重新審核（查詢中不列出）：" : "尚未審核（查詢中不列出）：");
            lines.AddRange(Fields(w, []));
        }
        else if (r.Versions.Count > 0)
        {
            lines.Add($"停用前的最後一版：第 {r.Versions[^1].Rev} 版");
            lines.AddRange(Fields(r.Versions[^1].Content, []));
        }
        return string.Join("\n", lines);
    }

    static IEnumerable<string> Fields(IncidentContent c, HashSet<string> changed) =>
        IncidentFields.All.Where(f => f.Key != IncidentFields.Events).Select(f =>
            (changed.Contains(f.Label) ? "★ " : "　 ") + $"{f.Label}："
            + (c.AiGuess.Contains(f.Key) ? "［AI 推測］" : "") + f.Text(c));

    static string Short(string s) => s.Length == 0 ? "（空白）" : s.ReplaceLineEndings(" / ");

    static string History(IncidentRecord r)
    {
        var lines = new List<string>();
        foreach (var v in r.Versions.AsEnumerable().Reverse())
        {
            lines.Add($"第 {v.Rev} 版　{v.ReviewedAt:yyyy-MM-dd HH:mm}　審核：{v.Reviewer}"
                + (v.Warnings.Count == 0 ? "" : $"　提醒：{string.Join("；", v.Warnings)}"));
            if (v.Rev == 1) lines.Add("　（第一版）");
            lines.AddRange(v.Changes.Select(c => $"　{c.Field}：{Short(c.Old)} → {Short(c.New)}"));
        }
        lines.Add("");
        lines.Add("經過：");
        lines.AddRange(r.History.AsEnumerable().Reverse().Select(h =>
            $"　{h.At:yyyy-MM-dd HH:mm}　{h.User}　{h.Action}" + (h.Note.Length == 0 ? "" : $"（{h.Note}）")));
        return string.Join("\n", lines);
    }

    // ---- 編輯 ----

    void LoadEditor(IncidentContent? c)
    {
        c ??= new IncidentContent();
        EditSymptom = c.Symptom;
        AiSymptom = c.AiGuess.Contains(IncidentFields.Symptom);
        EditFrom = c.OccurredFrom;
        EditTo = c.OccurredTo;
        EditPlant = c.Plant;
        EditCaseName = c.CaseName;
        EditAlarmCodes = string.Join("、", c.AlarmCodes);
        EditDevices = string.Join("、", c.Devices);
        EditDowntime = c.Downtime;
        EditHandler = c.Handler;
        EditCause = c.Cause;
        AiCause = c.AiGuess.Contains(IncidentFields.Cause);
        EditDisposal = c.Disposal;
        AiDisposal = c.AiGuess.Contains(IncidentFields.Disposal);
        EditExecutor = c.Executor;
        EditResult = c.Result;
    }

    /// <summary>編輯畫面目前的內容（事件以 RecordEvents 為準）。</summary>
    IncidentContent EditorContent(IncidentContent basis) => basis with
    {
        Symptom = EditSymptom.Trim(),
        OccurredFrom = EditFrom.Trim(),
        OccurredTo = EditTo.Trim(),
        Plant = EditPlant.Trim(),
        CaseName = EditCaseName.Trim(),
        Events = RecordEvents.ToList(),
        AlarmCodes = SplitList(EditAlarmCodes),
        Devices = SplitList(EditDevices),
        Downtime = EditDowntime.Trim(),
        Handler = EditHandler.Trim(),
        Cause = EditCause.Trim(),
        Disposal = EditDisposal.Trim(),
        Executor = EditExecutor.Trim(),
        Result = EditResult.Trim(),
        AiGuess = new[] { (IncidentFields.Symptom, AiSymptom), (IncidentFields.Cause, AiCause), (IncidentFields.Disposal, AiDisposal) }
            .Where(x => x.Item2).Select(x => x.Item1).ToList(),
    };

    static List<string> SplitList(string text) =>
        text.Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is { } n && n.StartsWith("Edit", StringComparison.Ordinal) || e.PropertyName is nameof(AiSymptom) or nameof(AiCause) or nameof(AiDisposal))
            UpdateCheck();
    }

    bool CanCreateFromCase() => _store is not null && _caseName is not null;

    /// <summary>從目前的案件建立草稿：帶入廠別與案件名稱；有選取的事件時一併加入。</summary>
    [RelayCommand(CanExecute = nameof(CanCreateFromCase))]
    void NewFromCase()
    {
        var content = new IncidentContent { Plant = _plant, CaseName = _caseName! };
        if (PickEvent() is { } pick) content = WithEvent(content, pick);
        Create(IncidentSource.Case, content);
    }

    /// <summary>整理過去的維修紀錄：手動輸入原文（匯入待格式確認後另外處理）。</summary>
    [RelayCommand(CanExecute = nameof(HasStore))]
    void NewMaintenance() => Create(IncidentSource.Maintenance, new IncidentContent { Plant = _plant });

    void Create(IncidentSource source, IncidentContent content)
    {
        var record = IncidentWorkflow.Create(source, content, UserName, Clock());
        if (Write(record, null)) Status = $"已建立 {record.Id}（草稿，列在待審核清單）。";
    }

    bool CanAddEvent() => IsEditing;

    /// <summary>把事件清單中選取的事件加入紀錄（保存時間、模組與遮罩後的訊息），並帶入它的警報代碼與設備編號。</summary>
    [RelayCommand(CanExecute = nameof(CanAddEvent))]
    void AddSelectedEvent()
    {
        if (PickEvent() is not { } pick)
        {
            Status = "請先在事件清單選取事件。";
            return;
        }
        if (!string.Equals(EditCaseName.Trim(), _caseName, StringComparison.Ordinal) && EditCaseName.Trim().Length > 0)
        {
            Status = $"這筆紀錄引用的是案件「{EditCaseName}」，不能加入目前案件的事件。";
            return;
        }
        if (RecordEvents.Any(e => e.StableId == pick.Event.StableId))
        {
            Status = "這個事件已經在紀錄中。";
            return;
        }
        var merged = WithEvent(EditorContent(Current!.Record.Work!), pick);
        if (EditCaseName.Trim().Length == 0) merged = merged with { CaseName = _caseName ?? "" };
        RecordEvents.Clear();
        foreach (var e in merged.Events) RecordEvents.Add(e);
        EditCaseName = merged.CaseName;
        EditFrom = merged.OccurredFrom;
        EditTo = merged.OccurredTo;
        EditAlarmCodes = string.Join("、", merged.AlarmCodes);
        EditDevices = string.Join("、", merged.Devices);
        Status = $"已加入事件 #{pick.Event.Sequence}（按「存檔」後保存）。";
    }

    /// <summary>加入事件：依時間排序；警報代碼與設備編號沒有的才加；發生時間空白時以事件時間帶入。</summary>
    static IncidentContent WithEvent(IncidentContent c, IncidentEventPick pick)
    {
        var events = c.Events.Append(pick.Event).OrderBy(e => e.Time, StringComparer.Ordinal).ThenBy(e => e.Sequence).ToList();
        return c with
        {
            Events = events,
            AlarmCodes = c.AlarmCodes.Concat(pick.AlarmCodes).Distinct(StringComparer.Ordinal).ToList(),
            Devices = c.Devices.Concat(pick.Devices).Distinct(StringComparer.Ordinal).ToList(),
            OccurredFrom = c.OccurredFrom.Length > 0 ? c.OccurredFrom : events[0].Time,
            OccurredTo = c.OccurredTo.Length > 0 && string.CompareOrdinal(c.OccurredTo, pick.Event.Time) >= 0 ? c.OccurredTo : events[^1].Time,
        };
    }

    bool CanRemoveEvent() => IsEditing && SelectedRecordEvent is not null;

    [RelayCommand(CanExecute = nameof(CanRemoveEvent))]
    void RemoveEvent()
    {
        RecordEvents.Remove(SelectedRecordEvent!);
        UpdateCheck();
    }

    bool CanGoToEvent() => SelectedRecordEvent is not null;

    /// <summary>跳到事件：只在引用的案件就是目前開啟的案件時；以穩定識別碼尋找。</summary>
    [RelayCommand(CanExecute = nameof(CanGoToEvent))]
    void GoToEvent()
    {
        var caseName = IsEditing ? EditCaseName.Trim() : (Current is { } c ? (IncidentWorkflow.Effective(c.Record)?.Content ?? c.Record.Work ?? c.Record.Versions.LastOrDefault()?.Content)?.CaseName : null) ?? "";
        if (!string.Equals(caseName, _caseName, StringComparison.Ordinal))
            Status = $"引用的案件「{caseName}」不是目前開啟的案件，無法跳到事件；仍可閱讀紀錄中保存的內容。";
        else if (!GoToStableId(SelectedRecordEvent!.StableId))
            Status = $"案件中找不到這個事件（{SelectedRecordEvent.StableId}），可能已重新匯入或刪除。";
    }

    [RelayCommand(CanExecute = nameof(IsEditing))]
    void Save()
    {
        var r = Current!;
        if (Write(IncidentWorkflow.SaveWork(r.Record, EditorContent(r.Record.Work!), UserName, Clock()), r.Stamp))
            Status = $"已存檔 {r.Record.Id}。";
    }

    /// <summary>審核通過：先套用編輯中的內容；有擋下的原因時不通過；有提醒時先確認。</summary>
    [RelayCommand(CanExecute = nameof(IsEditing))]
    void Approve()
    {
        var r = Current!;
        var now = Clock();
        var record = r.Record with { Work = EditorContent(r.Record.Work!) };
        var check = IncidentWorkflow.Check(record, UserName);
        if (!check.CanApprove)
        {
            Status = "不能審核通過：" + string.Join("；", check.Blocks);
            return;
        }
        if (check.Warnings.Count > 0 && !_confirm("審核異常紀錄", $"有以下提醒：\n・{string.Join("\n・", check.Warnings)}\n\n仍要審核通過嗎？"))
            return;
        if (IncidentFields.Diff(r.Record.Work!, record.Work!).Count > 0) record = IncidentWorkflow.SaveWork(r.Record, record.Work!, UserName, now);
        var approved = IncidentWorkflow.Approve(record, UserName, now);
        if (Write(approved, r.Stamp)) Status = $"{r.Record.Id} 審核通過，成為第 {approved.Versions[^1].Rev} 版。";
    }

    bool CanRevise() => Current?.Record.Status == IncidentStatus.Reviewed;

    [RelayCommand(CanExecute = nameof(CanRevise))]
    void Revise()
    {
        var r = Current!;
        if (Write(IncidentWorkflow.Revise(r.Record, UserName, Clock()), r.Stamp))
            Status = $"{r.Record.Id} 修訂中：審核通過前，查詢仍顯示第 {r.Record.Versions[^1].Rev} 版。";
    }

    bool CanCancelRevise() => Current?.Record.Status == IncidentStatus.Revising;

    [RelayCommand(CanExecute = nameof(CanCancelRevise))]
    void CancelRevise()
    {
        var r = Current!;
        if (!_confirm("放棄修改", $"放棄 {r.Record.Id} 尚未審核的修改？修改的內容不會保留。")) return;
        if (Write(IncidentWorkflow.CancelRevise(r.Record, UserName, Clock()), r.Stamp)) Status = $"已放棄 {r.Record.Id} 的修改。";
    }

    bool CanDisable() => Current is { } c && c.Record.Status != IncidentStatus.Disabled && !string.IsNullOrWhiteSpace(DisableReason);

    /// <summary>停用（附理由，不刪除）。</summary>
    [RelayCommand(CanExecute = nameof(CanDisable))]
    void Disable()
    {
        var r = Current!;
        if (!_confirm("停用異常紀錄", $"停用 {r.Record.Id}？查詢中不再列出，之後可以恢復（退回草稿重新審核）。"
                + (r.Record.Status == IncidentStatus.Revising ? "\n尚未審核的修改不會保留。" : ""))) return;
        if (Write(IncidentWorkflow.Disable(r.Record, DisableReason, UserName, Clock()), r.Stamp)) Status = $"已停用 {r.Record.Id}。";
    }

    bool CanRestore() => Current?.Record.Status == IncidentStatus.Disabled;

    /// <summary>恢復：退回草稿，重新審核通過後成為新的一版。</summary>
    [RelayCommand(CanExecute = nameof(CanRestore))]
    void Restore()
    {
        var r = Current!;
        if (Write(IncidentWorkflow.Restore(r.Record, UserName, Clock()), r.Stamp))
            Status = $"已恢復 {r.Record.Id}：退回草稿，重新審核通過前查詢中不列出。";
    }

    /// <summary>存檔並重新整理；檔案已被別人修改或無法寫入時不覆寫，說明原因。</summary>
    bool Write(IncidentRecord record, string? stamp)
    {
        try
        {
            var saved = _store!.Save(record, stamp);
            var i = _records.FindIndex(r => r.Record.Id == record.Id);
            if (i >= 0) _records[i] = saved;
            else _records.Add(saved);
            RefreshLists();
            Select(record.Id);
            return true;
        }
        catch (IncidentConflictException ex)
        {
            Status = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "存檔失敗：" + ex.Message;
        }
        return false;
    }

    void NotifyCommands()
    {
        NewFromCaseCommand.NotifyCanExecuteChanged();
        NewMaintenanceCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        ApproveCommand.NotifyCanExecuteChanged();
        ReviseCommand.NotifyCanExecuteChanged();
        CancelReviseCommand.NotifyCanExecuteChanged();
        DisableCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
        AddSelectedEventCommand.NotifyCanExecuteChanged();
        RemoveEventCommand.NotifyCanExecuteChanged();
        GoToEventCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsEditing));
    }
}
