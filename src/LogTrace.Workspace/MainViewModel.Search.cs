using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogTrace.Engine.Events;
using LogTrace.Engine.Search;

namespace LogTrace.Workspace;

/// <summary>可以勾選的項目（模組、Logger、等級、旗標）。</summary>
public sealed partial class SelectableItem(string name, string label) : ObservableObject
{
    public string Name { get; } = name;
    public string Label { get; } = label;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>
/// 搜尋（ADR-0010）：三個輸入框、篩選條件、兩種結果模式、符合筆數與醒目標示、最近的搜尋。
/// 按「搜尋」才查詢；已建好全文索引的日期依規則用索引，其餘掃描（ADR-0011 第 6 點）。搜尋時顯示進度，可以取消。
/// </summary>
public sealed partial class MainViewModel
{
    const string TimeFormat = "yyyy-MM-dd HH:mm:ss.ffff";

    HashSet<long> _matches = [];
    SearchQuery? _lastQuery;

    [ObservableProperty]
    public partial string SearchAllOf { get; set; } = "";

    [ObservableProperty]
    public partial string SearchAnyOf { get; set; } = "";

    [ObservableProperty]
    public partial string SearchNoneOf { get; set; } = "";

    [ObservableProperty]
    public partial bool SearchCaseSensitive { get; set; }

    /// <summary>正規表示式（進階選項，預設關閉）：三個輸入框中的每個關鍵字都是正規表示式。</summary>
    [ObservableProperty]
    public partial bool SearchRegex { get; set; }

    CancellationTokenSource? _searchCts;

    /// <summary>搜尋中（可以取消）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelSearchCommand))]
    public partial bool IsSearching { get; private set; }

    /// <summary>搜尋的進度（0～1）。</summary>
    [ObservableProperty]
    public partial double SearchProgress { get; private set; }

    bool CanCancelSearch() => IsSearching;

    /// <summary>取消搜尋；之前的結果維持不變。</summary>
    [RelayCommand(CanExecute = nameof(CanCancelSearch))]
    void CancelSearch() => _searchCts?.Cancel();

    /// <summary>時間區間（含起點、不含終點），格式 yyyy-MM-dd HH:mm:ss（可以省略時間或小數）；空白表示不限。</summary>
    [ObservableProperty]
    public partial string SearchFrom { get; set; } = "";

    [ObservableProperty]
    public partial string SearchTo { get; set; } = "";

    public ObservableCollection<SelectableItem> ModuleChoices { get; } = [];
    public ObservableCollection<SelectableItem> LoggerChoices { get; } = [];
    public ObservableCollection<SelectableItem> LevelChoices { get; } = [];
    public ObservableCollection<SelectableItem> FlagChoices { get; } =
        new(FlagInfo.All.Select(f => new SelectableItem(((int)f.Flag).ToString(CultureInfo.InvariantCulture), $"{f.Glyph} {f.Name}")));

    // 解析欄位（#23）
    public IReadOnlyList<FieldFilterOption> FilterOptions => FieldFilterOption.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsElementFilter), nameof(IsValueFilter))]
    public partial FieldFilterOption SelectedFilter { get; set; } = FieldFilterOption.All[0];

    public bool IsElementFilter => SelectedFilter.Kind == FieldFilterKind.Element;
    public bool IsValueFilter => SelectedFilter.Kind is FieldFilterKind.Command or FieldFilterKind.Pallet or FieldFilterKind.Bin;

    /// <summary>命令、棧板、桶編號。</summary>
    [ObservableProperty]
    public partial string FilterValue { get; set; } = "";

    /// <summary>目前案件所屬廠別的 Layout 中，有別名的元素。</summary>
    public ObservableCollection<ElementChoiceViewModel> ElementChoices { get; } = [];

    [ObservableProperty]
    public partial ElementChoiceViewModel? SelectedFilterElement { get; set; }

    /// <summary>false＝只顯示符合的事件（預設）；true＝顯示全部事件並標示符合的（ADR-0010 第 4 點）。</summary>
    [ObservableProperty]
    public partial bool ShowAllWithMarks { get; set; }

    /// <summary>搜尋結果的說明（符合筆數、耗時）；沒有搜尋時為 null。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearSearchCommand), nameof(NextMatchCommand), nameof(PreviousMatchCommand))]
    public partial string? SearchStatus { get; private set; }

    [ObservableProperty]
    public partial long MatchCount { get; private set; }

    /// <summary>最近的搜尋（最多 20 次，依使用者保存）。</summary>
    public ObservableCollection<SavedSearch> RecentSearches { get; } = [];

    /// <summary>選擇一筆最近的搜尋：把條件填回輸入框（不會立即查詢）。</summary>
    [ObservableProperty]
    public partial SavedSearch? SelectedRecentSearch { get; set; }

    partial void OnSelectedRecentSearchChanged(SavedSearch? value)
    {
        if (value is null) return;
        (SearchAllOf, SearchAnyOf, SearchNoneOf, SearchCaseSensitive, SearchRegex, SearchFrom, SearchTo) =
            (value.AllOf, value.AnyOf, value.NoneOf, value.CaseSensitive, value.Regex, value.From, value.To);
        foreach (var m in ModuleChoices) m.IsSelected = value.Modules.Contains(m.Name);
        foreach (var l in LoggerChoices) l.IsSelected = value.Loggers.Contains(l.Name);
        foreach (var l in LevelChoices) l.IsSelected = value.Levels.Contains(l.Name);
        foreach (var f in FlagChoices) f.IsSelected = (value.Flags & int.Parse(f.Name, CultureInfo.InvariantCulture)) != 0;
        SelectedFilter = FieldFilterOption.Of(value.FieldKind);
        FilterValue = value.FieldKind == FieldFilterKind.Element ? "" : value.FieldValue;
        SelectedFilterElement = ElementChoices.FirstOrDefault(e => e.Name == value.FieldValue);
    }

    partial void OnShowAllWithMarksChanged(bool value)
    {
        if (_lastQuery is not null) ShowSearchResult();
    }

    void LoadRecentSearches()
    {
        RecentSearches.Clear();
        foreach (var s in _settings.RecentSearches) RecentSearches.Add(s);
    }

    /// <summary>開啟案件後，列出案件中有的模組、Logger、等級。</summary>
    void LoadSearchChoices(IReadOnlyList<EventRowViewModel> rows, IReadOnlyList<string> modules)
    {
        void Fill(ObservableCollection<SelectableItem> target, IEnumerable<string> names)
        {
            target.Clear();
            foreach (var n in names) target.Add(new SelectableItem(n, n));
        }
        Fill(ModuleChoices, modules);
        Fill(LoggerChoices, rows.Select(r => r.Logger).Where(l => l.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        Fill(LevelChoices, rows.Select(r => r.Level).Where(l => l.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        ResetSearch();
    }

    void ResetSearch()
    {
        _matches = [];
        _lastQuery = null;
        MatchCount = 0;
        SearchStatus = null;
        FocusedElement = null;
        Timeline.SetSearchMatches(_matches);
        foreach (var r in _allRows)
        {
            r.IsMatch = false;
            r.Highlights = [];
        }
    }

    static bool TryParseTime(string text, out DateTime? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        string[] formats = ["yyyy-MM-dd HH:mm:ss.ffff", "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd"];
        if (!DateTime.TryParseExact(text.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) return false;
        value = t;
        return true;
    }

    bool CanSearch() => _case is not null && !IsBusy;

    /// <summary>依目前的條件搜尋（ADR-0010）。結果依時間排序，與事件清單相同。</summary>
    [RelayCommand(CanExecute = nameof(CanSearch))]
    async Task SearchAsync()
    {
        if (!TryParseTime(SearchFrom, out var from) || !TryParseTime(SearchTo, out var to))
        {
            SearchStatus = "時間的格式不對，請用 yyyy-MM-dd HH:mm:ss（例如 2030-01-07 08:00:00）。";
            return;
        }
        IReadOnlyList<string> fieldValues = SelectedFilter.Kind switch
        {
            FieldFilterKind.Element => SelectedFilterElement?.Aliases ?? [],
            FieldFilterKind.None => [],
            _ => string.IsNullOrWhiteSpace(FilterValue) ? [] : [FilterValue.Trim()],
        };
        var query = new SearchQuery
        {
            AllOf = SearchAllOf, AnyOf = SearchAnyOf, NoneOf = SearchNoneOf, CaseSensitive = SearchCaseSensitive, Regex = SearchRegex,
            From = from, To = to,
            Modules = ModuleChoices.Where(m => m.IsSelected).Select(m => m.Name).ToList(),
            Loggers = LoggerChoices.Where(m => m.IsSelected).Select(m => m.Name).ToList(),
            Levels = LevelChoices.Where(m => m.IsSelected).Select(m => m.Name).ToList(),
            Flags = FlagChoices.Where(f => f.IsSelected).Aggregate(EventFlags.None, (a, f) => a | (EventFlags)int.Parse(f.Name, CultureInfo.InvariantCulture)),
            FieldKinds = fieldValues.Count > 0 ? SelectedFilter.FieldKinds : [],
            FieldValues = fieldValues,
        };
        if (SelectedFilter.Kind != FieldFilterKind.None && fieldValues.Count == 0)
        {
            SearchStatus = SelectedFilter.Kind == FieldFilterKind.Element ? "請選擇 Layout 元素。" : $"請輸入{SelectedFilter.Label}。";
            return;
        }

        if (query.Validate() is { } error)
        {
            SearchStatus = error;
            return;
        }

        var c = _case!;
        var previous = SearchStatus;
        using var cts = _searchCts = new CancellationTokenSource();
        IsBusy = true;
        IsSearching = true;
        SearchProgress = 0;
        SearchStatus = "搜尋中…";
        var progress = new Progress<double>(p => SearchProgress = p);
        try
        {
            var clock = Stopwatch.StartNew();
            SearchOutcome outcome;
            try
            {
                outcome = await Task.Run(() => c.Search(query, SearchPath.Auto, progress, cts.Token));
            }
            catch (OperationCanceledException)
            {
                SearchStatus = _lastQuery is null ? "已取消搜尋。" : $"已取消搜尋；以下仍是上一次的結果。{previous}";
                return;
            }
            catch (ArgumentException ex)
            {
                SearchStatus = ex.Message;
                return;
            }
            clock.Stop();
            var seqs = outcome.Sequences;
            _matches = seqs.ToHashSet();
            _lastQuery = query;
            MatchCount = _matches.Count;
            foreach (var r in _allRows)
            {
                r.IsMatch = _matches.Contains(r.Sequence);
                r.Highlights = r.IsMatch ? query.FindMatches(r.Event.Message) : [];
            }
            ShowSearchResult();
            Timeline.SetSearchMatches(_matches);
            FocusedElement = null;
            var how = outcome.ByFields ? "先以解析欄位縮小範圍"
                : outcome.IndexedDays == 0 ? "掃描" : outcome.ScannedDays == 0 ? "全文索引" : $"全文索引 {outcome.IndexedDays} 天、掃描 {outcome.ScannedDays} 天";
            SearchStatus = $"符合 {MatchCount:N0} 筆（共 {_allRows.Count:N0} 筆，{clock.Elapsed.TotalSeconds:N2} 秒，{how}）";
            Remember(query);
        }
        finally
        {
            _searchCts = null;
            IsSearching = false;
            IsBusy = false;
        }
    }

    void Remember(SearchQuery q)
    {
        var saved = new SavedSearch
        {
            AllOf = q.AllOf, AnyOf = q.AnyOf, NoneOf = q.NoneOf, CaseSensitive = q.CaseSensitive, Regex = q.Regex,
            From = SearchFrom.Trim(), To = SearchTo.Trim(),
            Modules = q.Modules, Loggers = q.Loggers, Levels = q.Levels, Flags = (int)q.Flags,
            FieldKind = SelectedFilter.Kind,
            FieldValue = SelectedFilter.Kind == FieldFilterKind.Element ? SelectedFilterElement?.Name ?? "" : FilterValue.Trim(),
        };
        SaveSettings(_settings.WithSearch(saved));
        LoadRecentSearches();
    }

    /// <summary>依結果模式顯示：只顯示符合的，或顯示全部並標示符合的。選取的事件仍在清單中時保持選取。</summary>
    void ShowSearchResult() => ShowRows(ShowAllWithMarks ? _allRows : _allRows.Where(r => r.IsMatch));

    bool CanClearSearch() => SearchStatus is not null;

    /// <summary>清除搜尋：顯示全部事件、取消醒目標示。輸入框的內容保留。</summary>
    [RelayCommand(CanExecute = nameof(CanClearSearch))]
    void ClearSearch()
    {
        ResetSearch();
        ShowRows(_allRows);
    }

    bool CanMoveMatch() => _lastQuery is not null && MatchCount > 0;

    /// <summary>下一筆符合的事件（Ctrl＋→，ADR-0008 第 1 點）。</summary>
    [RelayCommand(CanExecute = nameof(CanMoveMatch))]
    void NextMatch() => MoveMatch(+1);

    /// <summary>上一筆符合的事件（Ctrl＋←）。</summary>
    [RelayCommand(CanExecute = nameof(CanMoveMatch))]
    void PreviousMatch() => MoveMatch(-1);

    void MoveMatch(int direction)
    {
        int index = SelectedEvent is null ? (direction > 0 ? -1 : Events.Count) : Events.IndexOf(SelectedEvent);
        for (int i = index + direction; i >= 0 && i < Events.Count; i += direction)
            if (Events[i].IsMatch)
            {
                SelectedEvent = Events[i];
                return;
            }
    }

    void ShowRows(IEnumerable<EventRowViewModel> rows)
    {
        var selected = SelectedEvent;
        Events.Clear();
        foreach (var r in rows) Events.Add(r);
        SelectedEvent = selected is not null && Events.Contains(selected) ? selected : Events.FirstOrDefault();
    }

    // ---- 個人資料遮罩（ADR-0009 第 1 點） ----

    /// <summary>選取的事件含有遮罩的個人資料（電話號碼）。</summary>
    public bool SelectedHasMasked => SelectedEvent is { } r && _masker.HasMatches(r.Event.Message);

    /// <summary>暫時顯示選取事件的完整電話號碼（點一下才顯示；換一筆事件就恢復遮罩）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedDetail), nameof(SelectedDetailSegments))]
    public partial bool RevealSensitive { get; set; }

    partial void OnRevealSensitiveChanged(bool value) => UpdateRawLog(SelectedEvent);

    [RelayCommand]
    void ToggleReveal() => RevealSensitive = !RevealSensitive;

    string Display(string text) => RevealSensitive ? EventRowViewModel.DisplayText(text) : _masker.Mask(EventRowViewModel.DisplayText(text));
}
