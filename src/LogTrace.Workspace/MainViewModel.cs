using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogTrace.Engine.Cases;
using LogTrace.Engine.Import;
using LogTrace.Engine.Parsing;
using LogTrace.Engine.Playback;
using LogTrace.Engine.Search;
using LogTrace.Workspace.Playback;
using LogTrace.Engine.Plants;

namespace LogTrace.Workspace;

/// <summary>
/// 主畫面：案件管理（存放位置、案件清單、建立、開啟、關閉、刪除）、匯入、
/// 事件清單（混合／分軌檢視）與選取、原始 LOG 面板。
/// 「目前時刻」、搜尋、回放等狀態在後續 ticket 加入。
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    const int PageSize = 10_000;

    /// <summary>原始 LOG 面板顯示選取事件前後各幾行。</summary>
    public const int RawContextLines = 50;

    readonly SettingsStore _settingsStore;
    readonly IUserInteraction _ui;
    readonly string _defaultCasesRoot;
    readonly Func<string, bool> _isInOneDrive;
    readonly string _plantsRoot;
    AppSettings _settings;
    CaseLibrary _library;
    Case? _case;
    List<EventRowViewModel> _allRows = [];
    TextMasker _masker = TextMasker.None;
    IReadOnlyList<EventField> _selectedFields = [];

    /// <param name="defaultCasesRoot">設定中沒有指定存放位置時使用（預設 %LOCALAPPDATA%\LogTrace\Cases）。</param>
    /// <param name="isInOneDrive">判斷位置是否在 OneDrive 同步資料夾內；預設依本機環境判斷。</param>
    /// <param name="plantsRoot">廠別設定檔的存放位置；預設與預設案件位置同一層的 Plants。</param>
    public MainViewModel(SettingsStore settingsStore, IUserInteraction ui, string defaultCasesRoot, Func<string, bool>? isInOneDrive = null,
        string? plantsRoot = null)
    {
        _plantsRoot = plantsRoot ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(defaultCasesRoot))!, "Plants");
        _settingsStore = settingsStore;
        _ui = ui;
        _defaultCasesRoot = defaultCasesRoot;
        _isInOneDrive = isInOneDrive ?? OneDriveDetector.IsInOneDrive;
        _settings = settingsStore.Load();
        _library = new CaseLibrary(_settings.CasesRoot ?? defaultCasesRoot);
        UpdateCasesRoot();
        RefreshCases();
        RefreshPlants();
        Playback.GoToEvent = GoToEvent;
        LoadRecentSearches();
    }

    /// <summary>Layout 回放（ADR-0008 第 6 點）；目前時刻＝事件清單選取的事件。</summary>
    public PlaybackViewModel Playback { get; } = new();

    /// <summary>選取指定的事件（例如從 Layout 回放跳到造成狀態的事件）；不在搜尋結果中時改為「顯示全部並標示符合的」。</summary>
    void GoToEvent(long sequence)
    {
        if (_allRows.FirstOrDefault(r => r.Sequence == sequence) is not { } row) return;
        if (!Events.Contains(row)) ShowAllWithMarks = true;
        if (!Events.Contains(row)) ShowRows(_allRows);
        SelectedEvent = row;
    }

    // ---- 案件管理 ----

    /// <summary>案件存放位置（ADR-0011 第 3 點）。</summary>
    [ObservableProperty]
    public partial string CasesRoot { get; private set; } = "";

    /// <summary>存放位置在 OneDrive 同步資料夾內時的警告；沒有問題時為 null。</summary>
    [ObservableProperty]
    public partial string? CasesRootWarning { get; private set; }

    /// <summary>案件清單：最近使用的在前（依使用順序），其餘依名稱。</summary>
    public ObservableCollection<CaseListItemViewModel> Cases { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenSelectedCaseCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCaseCommand))]
    public partial CaseListItemViewModel? SelectedCase { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCaseCommand))]
    public partial string NewCaseName { get; set; } = "";

    /// <summary>建立案件時可以選的廠別（ADR-0002 第 7 點）：內建預設值，以及存放位置下的廠別設定檔。</summary>
    public ObservableCollection<PlantChoiceViewModel> Plants { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCaseCommand))]
    public partial PlantChoiceViewModel? SelectedPlant { get; set; }

    /// <summary>目前案件所屬的廠別與修訂號。</summary>
    [ObservableProperty]
    public partial string? CurrentCasePlant { get; private set; }

    [ObservableProperty]
    public partial string? CurrentCaseName { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloseCaseCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenCaseFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    [NotifyCanExecuteChangedFor(nameof(EditTimeCorrectionCommand))]
    public partial string? CurrentCaseFolder { get; private set; }

    // ---- 匯入 ----

    /// <summary>匯入來源：資料夾、7z 或 zip（ADR-0002 第 2 點）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    public partial string ImportSource { get; set; } = "";

    [ObservableProperty]
    public partial string Status { get; private set; } = "請建立或開啟案件。";

    /// <summary>由畫面回報的訊息（例如版面檔無法使用）。</summary>
    public void ShowStatus(string message) => Status = message;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCaseCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenSelectedCaseCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCaseCommand))]
    [NotifyCanExecuteChangedFor(nameof(CloseCaseCommand))]
    [NotifyCanExecuteChangedFor(nameof(ChangeCasesRootCommand))]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyNewerPlantCommand), nameof(DismissNewerPlantCommand))]
    [NotifyCanExecuteChangedFor(nameof(EditTimeCorrectionCommand))]
    public partial bool IsBusy { get; private set; }

    // ---- 事件清單與原始 LOG ----

    public ObservableCollection<EventRowViewModel> Events { get; } = [];

    /// <summary>案件中出現的模組，依廠別設定的模組順序；分軌檢視每個模組一欄。</summary>
    public ObservableCollection<string> Modules { get; } = [];

    /// <summary>旗標圖例（ADR-0008 第 5 點）。</summary>
    public IReadOnlyList<FlagInfo> FlagLegend => FlagInfo.All;

    /// <summary>false＝混合檢視（預設，所有模組依時間混合）；true＝分軌檢視（每個模組一欄）。切換時選取的事件不變。</summary>
    [ObservableProperty]
    public partial bool IsTrackView { get; set; }

    /// <summary>原始 LOG 面板：選取事件所在檔案中，事件附近的行，依原始行序。</summary>
    public ObservableCollection<RawLineViewModel> RawLines { get; } = [];

    [ObservableProperty]
    public partial string? RawFile { get; private set; }

    /// <summary>原始 LOG 面板中，選取事件的起始行；畫面捲動到這一行。</summary>
    [ObservableProperty]
    public partial RawLineViewModel? RawSelectedLine { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedDetail), nameof(SelectedDetailSegments), nameof(SelectedHasMasked))]
    public partial EventRowViewModel? SelectedEvent { get; set; }

    /// <summary>選取事件的詳細資訊：原始位置、穩定識別碼、旗標、解析欄位與完整訊息。</summary>
    public string SelectedDetail => SelectedEvent is not { Event: var e } row
        ? ""
        : $"來源：{EventRowViewModel.FormatSource(e.Source)}\n識別碼：{e.Id}\n原始時間：{e.RawTimestamp}"
          + (_case?.Info.Correction.For(e.Module) is { } offset && offset != TimeSpan.Zero
              ? $"\n已校正：{LogTrace.Engine.Events.TimeCorrection.Format(offset)}（校正前 {e.Timestamp - offset:yyyy-MM-dd HH:mm:ss.ffff}，校正後 {e.Timestamp:yyyy-MM-dd HH:mm:ss.ffff}）"
              : "")
          + (row.Flags.Count == 0 ? "" : "\n旗標：" + string.Join("、", row.Flags.Select(f => $"{f.Glyph} {f.Name}")))
          + (_selectedFields.Count == 0 ? "" : "\n解析欄位：" + string.Join("、", _selectedFields.Select(f => $"{FieldKinds.Label(f.Kind)} {f.Value}")))
          + (row.MatchNote is { } note ? $"\n搜尋：{note}" : "")
          + $"\n\n{Display(e.Message)}";

    /// <summary>詳細資訊分成一般與醒目的片段（搜尋的關鍵字醒目標示，ADR-0010 第 4 點）。</summary>
    public IReadOnlyList<TextSegment> SelectedDetailSegments
    {
        get
        {
            if (SelectedEvent is not { } row) return [];
            var detail = SelectedDetail;
            int offset = detail.Length - row.Event.Message.Length; // 訊息在最後，長度與原文相同
            return EventRowViewModel.Segments(detail, row.Highlights.Select(h => (h.Start + offset, h.Length)).ToList());
        }
    }

    partial void OnSelectedEventChanged(EventRowViewModel? value)
    {
        RevealSensitive = false;
        _selectedFields = value is not null && _case is not null ? _case.GetFields(value.Sequence) : [];
        UpdateRawLog(value);
        OnSelectionMovedMoment(value);
    }

    /// <summary>
    /// 案件所用的廠別設定（含 Layout）：案件保存的快照（ADR-0006 第 8 點）。
    /// 快照功能加入前建立的案件沒有快照，改從存放位置依廠別名稱找同一修訂的設定檔；找不到時為 null。
    /// </summary>
    PlantConfigDocument? FindPlantDocument(Case c)
    {
        if (c.PlantSnapshot is { } snapshot) return snapshot;
        if (c.Info.Plant.Revision == 0) return null;
        foreach (var path in new PlantConfigLibrary(_plantsRoot).List())
        {
            try
            {
                var doc = PlantConfigFile.Load(path);
                if (string.Equals(doc.Plant, c.Info.Plant.Name, StringComparison.Ordinal)) return doc;
            }
            catch (Exception ex) when (ex is PlantConfigFormatException or IOException or UnauthorizedAccessException)
            {
                // 讀不了的設定檔略過
            }
        }
        return null;
    }

    /// <summary>列出有別名的元素（篩選用）。</summary>
    void LoadElementChoices(PlantConfigDocument? doc)
    {
        ElementChoices.Clear();
        SelectedFilterElement = null;
        foreach (var e in doc?.Layout.Elements.Where(e => e.Aliases is { Count: > 0 }) ?? [])
            ElementChoices.Add(new ElementChoiceViewModel(e.Name ?? e.Id, Layout.LayoutElementTypes.Label(e.Type), e.Aliases!));
    }

    /// <summary>原始 LOG 面板與選取的事件同步（ADR-0008 第 4 點）：依原始行序顯示事件前後各 RawContextLines 行。</summary>
    void UpdateRawLog(EventRowViewModel? row)
    {
        RawSelectedLine = null;
        RawLines.Clear();
        if (row is null || _case is null)
        {
            RawFile = null;
            return;
        }
        var src = row.Event.Source;
        RawFile = src.RelativePath;
        try
        {
            int first = Math.Max(1, src.LineStart - RawContextLines);
            int count = src.LineEnd + RawContextLines - first + 1;
            foreach (var line in _case.ReadRawLines(src.RelativePath, first, count))
            {
                var vm = new RawLineViewModel(line.LineNo, Display(line.Text),
                    line.LineNo >= src.LineStart && line.LineNo <= src.LineEnd);
                RawLines.Add(vm);
                if (line.LineNo == src.LineStart) RawSelectedLine = vm;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "讀取原始 LOG 失敗：" + ex.Message;
        }
    }

    // ---- 案件管理的命令 ----

    void UpdateCasesRoot()
    {
        CasesRoot = _library.Root;
        CasesRootWarning = _isInOneDrive(_library.Root)
            ? "案件存放位置在 OneDrive 同步資料夾內。案件開啟時資料一直在變動，同步可能產生不完整的複本，也會把真實 LOG 上傳到雲端；建議改到不同步的位置。"
            : null;
    }

    void RefreshCases()
    {
        var recent = _settings.RecentCases;
        int? Rank(string folder)
        {
            for (int i = 0; i < recent.Count; i++)
                if (string.Equals(recent[i], folder, StringComparison.OrdinalIgnoreCase)) return i;
            return null;
        }
        var selected = SelectedCase?.Folder;
        Cases.Clear();
        foreach (var item in _library.List().Select(s => new CaseListItemViewModel(s, Rank(s.Folder)))
                     .OrderBy(c => c.RecentRank ?? int.MaxValue).ThenBy(c => c.Name, StringComparer.CurrentCulture))
            Cases.Add(item);
        SelectedCase = Cases.FirstOrDefault(c => string.Equals(c.Folder, selected, StringComparison.OrdinalIgnoreCase));
    }

    void SaveSettings(AppSettings settings)
    {
        _settings = settings;
        try
        {
            _settingsStore.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "儲存設定失敗：" + ex.Message;
        }
    }

    bool CanChangeCasesRoot() => !IsBusy;

    /// <summary>變更案件存放位置（ADR-0011 第 3 點）。已有的案件不會搬移。</summary>
    [RelayCommand(CanExecute = nameof(CanChangeCasesRoot))]
    void ChangeCasesRoot()
    {
        if (_ui.PickFolder("選擇案件存放位置") is not { } folder) return;
        _library = new CaseLibrary(folder);
        SaveSettings(_settings with { CasesRoot = _library.Root });
        UpdateCasesRoot();
        RefreshCases();
        Status = $"案件存放位置改為 {_library.Root}。已有的案件不會搬移。";
    }

    /// <summary>重新列出廠別設定檔（例如在 Layout 編輯器中新增或改名之後）；保留原本的選擇。</summary>
    public void RefreshPlants()
    {
        var selected = SelectedPlant?.Path;
        Plants.Clear();
        Plants.Add(PlantChoiceViewModel.BuiltIn);
        foreach (var path in new PlantConfigLibrary(_plantsRoot).List())
            Plants.Add(new PlantChoiceViewModel(Path.GetFileNameWithoutExtension(path), path));
        SelectedPlant = Plants.FirstOrDefault(p => string.Equals(p.Path, selected, StringComparison.OrdinalIgnoreCase)) ?? Plants[0];
        CheckNewerPlant();
    }

    // ---- 廠別設定的新版（ADR-0006 第 8 點） ----

    (PlantConfigDocument Document, string Path)? _newerPlant;
    int _dismissedRevision;

    /// <summary>目前案件的廠別設定有新版時的提示；沒有新版或選擇不套用時為 null。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyNewerPlantCommand), nameof(DismissNewerPlantCommand))]
    public partial string? NewerPlantNotice { get; private set; }

    /// <summary>檢查目前案件的廠別設定是否有新版（開啟案件、回到主畫面時）。</summary>
    void CheckNewerPlant()
    {
        _newerPlant = _case?.FindNewerPlant(new PlantConfigLibrary(_plantsRoot));
        NewerPlantNotice = _newerPlant is { } n && n.Document.Revision > _dismissedRevision
            ? $"廠別「{n.Document.Plant}」的設定有新版（修訂 {_case!.Info.Plant.Revision} → {n.Document.Revision}）。要套用到這個案件嗎？套用後會依新的設定重新解析事件。"
            : null;
    }

    bool CanAnswerNewerPlant() => NewerPlantNotice is not null && !IsBusy;

    /// <summary>套用新版：以新的設定重建事件、解析欄位與世界模型（稽核紀錄記下修訂號）。</summary>
    [RelayCommand(CanExecute = nameof(CanAnswerNewerPlant))]
    async Task ApplyNewerPlantAsync()
    {
        if (_newerPlant is not { } newer || _case is not { } c) return;
        IsBusy = true;
        Status = "套用新版的廠別設定中…";
        try
        {
            await Task.Run(() => c.ApplyPlant(newer.Document, newer.Path));
            _masker = new TextMasker(c.Info.Plant.MaskPatterns);
            CurrentCasePlant = $"{c.Info.Plant.Name}（修訂 {c.Info.Plant.Revision}）";
            await LoadEventsAsync(c);
            Status = $"已套用廠別設定修訂 {c.Info.Plant.Revision}：{Events.Count:N0} 筆事件。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            Status = "套用失敗：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
            CheckNewerPlant();
        }
    }

    /// <summary>不套用：維持案件的快照；同一個修訂不再提示（有更新的修訂時再提示）。</summary>
    [RelayCommand(CanExecute = nameof(CanAnswerNewerPlant))]
    void DismissNewerPlant()
    {
        _dismissedRevision = _newerPlant?.Document.Revision ?? 0;
        NewerPlantNotice = null;
    }

    bool CanCreateCase() => !IsBusy && !string.IsNullOrWhiteSpace(NewCaseName) && SelectedPlant is not null;

    /// <summary>以所選的廠別建立案件；之後的匯入使用該廠別的模組順序、已知 LOG 種類與編碼設定。</summary>
    [RelayCommand(CanExecute = nameof(CanCreateCase))]
    void CreateCase()
    {
        try
        {
            var path = SelectedPlant!.Path;
            var doc = path is null ? null : PlantConfigFile.Load(path);
            var plant = doc?.ToPlantConfig() ?? PlantConfig.Default;
            CloseCurrent();
            var c = _library.Create(NewCaseName, plant, doc, path); // 保存廠別設定的快照（ADR-0006 第 8 點）
            SetCurrent(c);
            Status = $"已建立案件「{c.Info.Name}」（廠別：{plant.Name}）。請選擇要匯入的 LOG（資料夾、7z 或 zip）。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException
                                       or PlantConfigFormatException)
        {
            Status = "建立案件失敗：" + ex.Message;
        }
    }

    bool CanUseSelectedCase() => !IsBusy && SelectedCase is not null;

    [RelayCommand(CanExecute = nameof(CanUseSelectedCase))]
    async Task OpenSelectedCaseAsync()
    {
        var folder = SelectedCase!.Folder;
        CloseCurrent();
        IsBusy = true;
        Status = "開啟案件中…";
        try
        {
            var c = await Task.Run(() => Case.Open(folder));
            SetCurrent(c);
            await LoadEventsAsync(c);
            Status = $"已開啟案件「{c.Info.Name}」：{Events.Count:N0} 筆事件。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
        {
            Status = "開啟案件失敗：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    bool CanCloseCase() => !IsBusy && CurrentCaseFolder is not null;

    /// <summary>關閉案件：之後可以安全地複製案件資料夾作為備份（ADR-0009 第 6 點）。</summary>
    [RelayCommand(CanExecute = nameof(CanCloseCase))]
    void CloseCase()
    {
        var name = CurrentCaseName;
        CloseCurrent();
        Status = $"已關閉案件「{name}」。現在可以複製案件資料夾作為備份。";
    }

    /// <summary>刪除案件（需確認）。只刪除案件中的複本，使用者原本的 LOG 不受影響（ADR-0009 第 6 點）。</summary>
    [RelayCommand(CanExecute = nameof(CanUseSelectedCase))]
    void DeleteSelectedCase()
    {
        var item = SelectedCase!;
        if (!_ui.Confirm("刪除案件",
                $"確定要刪除案件「{item.Name}」？\n\n位置：{item.Folder}\n\n" +
                "案件中的原始 LOG 複本、匯入紀錄都會刪除，無法復原。你原本的 LOG 檔案不受影響。"))
            return;
        try
        {
            if (string.Equals(CurrentCaseFolder, item.Folder, StringComparison.OrdinalIgnoreCase)) CloseCurrent();
            _library.Delete(item.Folder);
            SaveSettings(_settings.WithoutRecent(item.Folder));
            RefreshCases();
            Status = $"已刪除案件「{item.Name}」。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Status = "刪除案件失敗：" + ex.Message;
        }
    }

    bool CanOpenCaseFolder() => CurrentCaseFolder is not null;

    [RelayCommand(CanExecute = nameof(CanOpenCaseFolder))]
    void OpenCaseFolder() => _ui.OpenFolder(CurrentCaseFolder!);

    void SetCurrent(Case c)
    {
        _case = c;
        _masker = new TextMasker(c.Info.Plant.MaskPatterns);
        CurrentCaseName = c.Info.Name;
        CurrentCasePlant = c.Info.Plant.Revision == 0 ? c.Info.Plant.Name : $"{c.Info.Plant.Name}（修訂 {c.Info.Plant.Revision}）";
        CurrentCaseFolder = c.Folder;
        SaveSettings(_settings.WithRecent(c.Folder));
        _dismissedRevision = 0;
        CheckNewerPlant();
        UpdateTimeTexts();
        RefreshCases();
        SelectedCase = Cases.FirstOrDefault(i => string.Equals(i.Folder, c.Folder, StringComparison.OrdinalIgnoreCase));
    }

    void CloseCurrent()
    {
        SelectedEvent = null;
        Events.Clear();
        _allRows = [];
        ResetSearch();
        ElementChoices.Clear();
        Playback.Clear("請開啟案件。");
        ClearMoment();
        Modules.Clear();
        _case?.Dispose();
        _case = null;
        CurrentCaseName = null;
        CurrentCasePlant = null;
        CurrentCaseFolder = null;
        _newerPlant = null;
        NewerPlantNotice = null;
        UpdateTimeTexts();
    }

    // ---- 匯入的命令 ----

    bool CanImport() => !IsBusy && CurrentCaseFolder is not null && !string.IsNullOrWhiteSpace(ImportSource);

    /// <summary>
    /// 匯入（ADR-0002）：先準備（複製或解開、分類每個檔案），有內容衝突時讓使用者逐檔選擇，再套用並重新排序。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanImport))]
    async Task ImportAsync()
    {
        var c = _case!;
        var source = ImportSource.Trim();
        IsBusy = true;
        Status = "匯入中…";
        PreparedImport? prepared = null;
        try
        {
            prepared = await Task.Run(() => c.PrepareImport(source));
            IReadOnlySet<string> useNew = new HashSet<string>();
            if (prepared.Conflicts.Count > 0)
            {
                if (_ui.ResolveConflicts(prepared.Conflicts) is not { } chosen)
                {
                    Status = "已取消匯入，案件沒有變更。";
                    return;
                }
                useNew = chosen;
            }
            var p = prepared;
            var result = await Task.Run(() => c.ApplyImport(p, useNew));
            await LoadEventsAsync(c);
            Status = Summarize(result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or PlatformNotSupportedException or SevenZip.SevenZipException)
        {
            Status = "匯入失敗：" + ex.Message;
        }
        finally
        {
            prepared?.Dispose();
            IsBusy = false;
        }
    }

    static string Summarize(ImportResult r)
    {
        var parts = new List<string> { $"新增 {r.NewFiles}" };
        if (r.IdenticalFiles > 0) parts.Add($"相同略過 {r.IdenticalFiles}");
        if (r.ExtendedFiles > 0) parts.Add($"改用較完整的新檔 {r.ExtendedFiles}");
        if (r.ConflictsUsedNew > 0) parts.Add($"衝突改用新檔 {r.ConflictsUsedNew}");
        if (r.ConflictsKeptOld > 0) parts.Add($"衝突保留舊檔 {r.ConflictsKeptOld}");
        return $"已匯入 {r.FileCount} 個檔案（{string.Join("、", parts)}）；案件共 {r.EventCount:N0} 筆事件，耗時 {r.Elapsed.TotalSeconds:N1} 秒。"
            + (r.UnparsableFileCount == 0 ? "" : $"無法解析的檔案 {r.UnparsableFileCount} 個（沒有產生事件）。");
    }

    async Task LoadEventsAsync(Case c)
    {
        var masker = _masker;
        var (modules, rows) = await Task.Run(() => LoadAll(c, masker));
        SelectedEvent = null;
        Events.Clear();
        Modules.Clear();
        foreach (var m in modules) Modules.Add(m);
        _allRows = rows;
        LoadSearchChoices(rows, modules);
        foreach (var r in rows) Events.Add(r);
        var doc = FindPlantDocument(c);
        LoadElementChoices(doc);
        await LoadPlaybackAsync(c, doc, rows);
        SelectedEvent = Events.FirstOrDefault();
        SearchCommand.NotifyCanExecuteChanged();
    }

    /// <summary>建立世界模型（開啟案件時在記憶體中建立快照）並載入 Layout 回放。</summary>
    async Task LoadPlaybackAsync(Case c, PlantConfigDocument? doc, List<EventRowViewModel> rows)
    {
        var effects = await Task.Run(() => c.GetEffects());
        var changes = effects.Select(e => e.Sequence).ToList();
        if (doc is null)
        {
            Playback.Clear(c.Info.Plant.Revision == 0
                ? "這個案件使用內建預設的廠別設定，沒有 Layout。請在 Layout 編輯器建立廠別設定，並以它建立案件。"
                : $"找不到廠別「{c.Info.Plant.Name}」的設定檔，無法顯示 Layout。");
            LoadMoment(changes, []);
            return;
        }
        var presence = c.Info.Plant.PresenceSignal;
        var model = await Task.Run(() => new WorldModel(effects, doc.Layout, presence));
        var hasStateRules = c.Info.Plant.ParseRules.Any(r => r.Effect is { } e && e != EffectKinds.Signal);
        Playback.Load(model, doc.Layout, rows.Select(r => r.Event).ToList(), hasStateRules);
        var names = doc.Layout.Elements.ToDictionary(e => e.Id, e => e.Name ?? e.Id);
        LoadMoment(changes, model.AlarmPeriods().Select(a => ($"{names.GetValueOrDefault(a.ElementId, a.ElementId)} {a.Code}", a.StartSequence, a.EndSequence)));
    }

    static (List<string> Modules, List<EventRowViewModel> Rows) LoadAll(Case c, TextMasker masker)
    {
        var order = c.Info.Plant.ModuleOrder.ToList();
        var modules = c.GetFiles().Where(f => f.Active).Select(f => f.Module).Distinct(StringComparer.Ordinal)
            .OrderBy(m => order.Contains(m) ? order.IndexOf(m) : order.Count).ThenBy(m => m, StringComparer.Ordinal)
            .ToList();
        var rows = new List<EventRowViewModel>((int)Math.Min(c.EventCount, int.MaxValue));
        long next = 1;
        while (true)
        {
            var page = c.GetEvents(next, PageSize);
            if (page.Count == 0) break;
            rows.AddRange(page.Select(e => new EventRowViewModel(e, modules.IndexOf(e.Module), masker)));
            next = page[^1].Sequence + 1;
        }
        return (modules, rows);
    }

    /// <summary>開啟 Layout 編輯器，編輯廠別設定檔（ADR-0006）。</summary>
    [RelayCommand]
    void OpenLayoutEditor() => _ui.ShowLayoutEditor(new Layout.LayoutEditorViewModel(new PlantConfigLibrary(_plantsRoot), _ui, _library));

    [RelayCommand]
    void BrowseImportFolder()
    {
        if (_ui.PickFolder("選擇要匯入的 LOG 資料夾") is { } folder) ImportSource = folder;
    }

    [RelayCommand]
    void BrowseImportArchive()
    {
        if (_ui.PickArchive("選擇要匯入的 LOG 壓縮檔（7z 或 zip）") is { } file) ImportSource = file;
    }

    public void Dispose() => _case?.Dispose();
}
