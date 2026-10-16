using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogTrace.Workspace.Playback;

namespace LogTrace.Workspace;

/// <summary>
/// 目前時刻與回放操作（ADR-0008 第 1～4 點）：所有面板共用一個「目前時刻」；
/// 選取事件、點時間軸、播放、方向鍵都會改變它，事件清單、Layout、詳細資訊、原始 LOG、時間軸同步。
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>播放速度（ADR-0008 第 2 點）。</summary>
    public static IReadOnlyList<int> Speeds { get; } = [1, 2, 5, 20, 60];

    /// <summary>略過閒置時段：超過這麼久沒有狀態變化時，直接跳到下一個變化。</summary>
    public static readonly TimeSpan IdleThreshold = TimeSpan.FromSeconds(10);

    long[] _changes = [];
    long _momentSequence;
    bool _settingMoment;

    public TimelineViewModel Timeline { get; } = new();

    /// <summary>目前時刻；沒有開啟案件時為 null。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentTimeText))]
    public partial DateTime? CurrentTime { get; private set; }

    public string CurrentTimeText => CurrentTime is { } t ? t.ToString("yyyy-MM-dd HH:mm:ss.ffff", CultureInfo.InvariantCulture) : "";

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    [ObservableProperty]
    public partial int Speed { get; set; } = 1;

    /// <summary>略過閒置時段（預設開啟，可以關閉）。</summary>
    [ObservableProperty]
    public partial bool SkipIdle { get; set; } = true;

    /// <summary>「只看這個元素」的元素名稱；沒有時為 null。</summary>
    [ObservableProperty]
    public partial string? FocusedElement { get; private set; }

    /// <summary>選取的事件改變時（使用者在事件清單點選）：目前時刻移到該事件。</summary>
    void OnSelectionMovedMoment(EventRowViewModel? row)
    {
        if (_settingMoment || row is null) return;
        ApplyMoment(row.Event.Timestamp, row.Sequence);
    }

    void ApplyMoment(DateTime time, long sequence)
    {
        CurrentTime = time;
        _momentSequence = sequence;
        Playback.SetMoment(sequence);
        Timeline.SetCurrent(time);
        UpdateMomentPanels();
    }

    /// <summary>
    /// 跳到任意時刻（點時間軸、播放）：Layout 等顯示該時刻（含）之前所有事件套用後的狀態；
    /// 事件清單選取該時刻之前（含）最後一筆顯示中的事件。
    /// </summary>
    public void SeekTime(DateTime time)
    {
        if (_allRows.Count == 0) return;
        // 限制在案件範圍內；第一筆事件之前表示「案件開始」（所有元素都是未知）
        var first = _allRows[0].Event.Timestamp;
        time = time < first ? first.AddTicks(-1) : time > Timeline.End ? Timeline.End : time;
        var all = LastAtOrBefore(_allRows, time);
        var visible = LastAtOrBefore(Events, time);
        _settingMoment = true;
        try
        {
            SelectedEvent = visible;
        }
        finally
        {
            _settingMoment = false;
        }
        ApplyMoment(time, all?.Sequence ?? 0);
    }

    static EventRowViewModel? LastAtOrBefore(IList<EventRowViewModel> rows, DateTime time)
    {
        int lo = 0, hi = rows.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (rows[mid].Event.Timestamp <= time) lo = mid + 1;
            else hi = mid;
        }
        return lo == 0 ? null : rows[lo - 1];
    }

    /// <summary>載入案件後：狀態變化的事件與時間軸。</summary>
    void LoadMoment(IReadOnlyList<long> changes, IEnumerable<(string Label, long Start, long? End)> alarms)
    {
        _changes = changes.Distinct().Order().ToArray();
        Timeline.Load(_allRows, _changes, alarms);
        LoadCommands();
        IsPlaying = false;
        FocusedElement = null;
        if (_allRows.Count > 0) ApplyMoment(_allRows[0].Event.Timestamp, _allRows[0].Sequence);
    }

    void ClearMoment()
    {
        _changes = [];
        _momentSequence = 0;
        IsPlaying = false;
        FocusedElement = null;
        CurrentTime = null;
        Timeline.Clear();
        Commands.Clear();
        CommandEvents.Clear();
        UnmappedNearby.Clear();
    }

    /// <summary>目前可以移動到的事件序號：「只看這個元素」或搜尋中時只限顯示中的事件。</summary>
    HashSet<long>? VisibleSequences => Events.Count == _allRows.Count ? null : Events.Select(e => e.Sequence).ToHashSet();

    void MoveTo(long sequence)
    {
        if (_allRows.FirstOrDefault(r => r.Sequence == sequence) is { } row) SelectedEvent = row;
    }

    // ---- 方向鍵（ADR-0008 第 1 點） ----

    /// <summary>→：下一個狀態變化（會改變任何元素狀態的事件）。到最後時停在原地。</summary>
    [RelayCommand]
    void NextChange()
    {
        var visible = VisibleSequences;
        foreach (var s in _changes)
            if (s > _momentSequence && (visible is null || visible.Contains(s)))
            {
                MoveTo(s);
                return;
            }
        Status = _changes.Length == 0 ? "這個案件沒有狀態變化（廠別設定的規則中沒有效果）。" : "已經是最後一個狀態變化。";
    }

    /// <summary>←：上一個狀態變化。到最前面時停在原地。</summary>
    [RelayCommand]
    void PreviousChange()
    {
        var visible = VisibleSequences;
        for (int i = _changes.Length - 1; i >= 0; i--)
            if (_changes[i] < _momentSequence && (visible is null || visible.Contains(_changes[i])))
            {
                MoveTo(_changes[i]);
                return;
            }
        Status = _changes.Length == 0 ? "這個案件沒有狀態變化（廠別設定的規則中沒有效果）。" : "已經是第一個狀態變化。";
    }

    /// <summary>Shift＋→：下一筆事件（顯示中的）。</summary>
    [RelayCommand]
    void NextEvent()
    {
        var next = Events.FirstOrDefault(e => e.Sequence > _momentSequence);
        if (next is null) Status = "已經是最後一筆事件。";
        else SelectedEvent = next;
    }

    /// <summary>Shift＋←：上一筆事件（顯示中的）。</summary>
    [RelayCommand]
    void PreviousEvent()
    {
        var previous = Events.LastOrDefault(e => e.Sequence < _momentSequence);
        if (previous is null) Status = "已經是第一筆事件。";
        else SelectedEvent = previous;
    }

    /// <summary>Home：案件開頭（顯示中的第一筆）。</summary>
    [RelayCommand]
    void GoToStart()
    {
        if (Events.Count > 0) SelectedEvent = Events[0];
    }

    /// <summary>End：案件結尾（顯示中的最後一筆）。</summary>
    [RelayCommand]
    void GoToEnd()
    {
        if (Events.Count > 0) SelectedEvent = Events[^1];
    }

    // ---- 播放（ADR-0008 第 2 點） ----

    /// <summary>空白鍵：播放／暫停。在結尾按播放時從頭開始。</summary>
    [RelayCommand]
    void TogglePlay()
    {
        if (_allRows.Count == 0) return;
        if (!IsPlaying && CurrentTime >= Timeline.End) SeekTime(Timeline.Start);
        IsPlaying = !IsPlaying;
    }

    /// <summary>
    /// 播放時，畫面的計時器每隔一段時間呼叫：目前時刻前進「經過的時間 × 速度」。
    /// 略過閒置時段時，若下一個狀態變化在 IdleThreshold 之後，直接跳到它。到案件結尾時停止。
    /// </summary>
    public void Tick(TimeSpan elapsed)
    {
        if (!IsPlaying || CurrentTime is not { } now) return;
        var next = now + elapsed * Speed;
        if (SkipIdle && NextChangeTime(now) is { } change && change - now > IdleThreshold && next < change) next = change;
        if (next >= Timeline.End)
        {
            SeekTime(Timeline.End);
            IsPlaying = false;
            Status = "已播放到案件結尾。";
            return;
        }
        SeekTime(next);
    }

    DateTime? NextChangeTime(DateTime now)
    {
        foreach (var s in _changes)
            if (s > _momentSequence && _allRows.FirstOrDefault(r => r.Sequence == s) is { } row && row.Event.Timestamp > now)
                return row.Event.Timestamp;
        return null;
    }

    // ---- 只看這個元素（ADR-0008 第 4 點） ----

    /// <summary>
    /// 「只看這個元素」：事件清單篩選成與 Layout 上選取的元素（含所有別名）相關的事件；
    /// 方向鍵也只在這些事件之間移動。以「清除搜尋」恢復。
    /// </summary>
    [RelayCommand]
    async Task FocusSelectedElementAsync()
    {
        if (Playback.SelectedElement is not { } element) return;
        if (element.Model.Aliases is not { Count: > 0 })
        {
            Status = $"「{element.Name}」沒有別名，無法對應到 LOG 中的事件。";
            return;
        }
        var choice = ElementChoices.FirstOrDefault(c => c.Name == element.Name)
            ?? new ElementChoiceViewModel(element.Name, element.TypeLabel, element.Model.Aliases);
        (SearchAllOf, SearchAnyOf, SearchNoneOf, SearchFrom, SearchTo) = ("", "", "", "", "");
        foreach (var item in ModuleChoices.Concat(LoggerChoices).Concat(LevelChoices).Concat(FlagChoices)) item.IsSelected = false;
        SelectedFilter = FieldFilterOption.Of(FieldFilterKind.Element);
        SelectedFilterElement = choice;
        ShowAllWithMarks = false;
        await SearchAsync();
        FocusedElement = element.Name;
        SearchStatus = $"只看「{element.Name}」：{MatchCount:N0} 筆事件";
    }
}
