using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogTrace.Engine.Cases;
using LogTrace.Engine.Import;
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
    AppSettings _settings;
    CaseLibrary _library;
    Case? _case;

    /// <param name="defaultCasesRoot">設定中沒有指定存放位置時使用（預設 %LOCALAPPDATA%\LogTrace\Cases）。</param>
    /// <param name="isInOneDrive">判斷位置是否在 OneDrive 同步資料夾內；預設依本機環境判斷。</param>
    public MainViewModel(SettingsStore settingsStore, IUserInteraction ui, string defaultCasesRoot, Func<string, bool>? isInOneDrive = null)
    {
        _settingsStore = settingsStore;
        _ui = ui;
        _defaultCasesRoot = defaultCasesRoot;
        _isInOneDrive = isInOneDrive ?? OneDriveDetector.IsInOneDrive;
        _settings = settingsStore.Load();
        _library = new CaseLibrary(_settings.CasesRoot ?? defaultCasesRoot);
        UpdateCasesRoot();
        RefreshCases();
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

    [ObservableProperty]
    public partial string? CurrentCaseName { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloseCaseCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenCaseFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    public partial string? CurrentCaseFolder { get; private set; }

    // ---- 匯入 ----

    /// <summary>匯入來源：資料夾、7z 或 zip（ADR-0002 第 2 點）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    public partial string ImportSource { get; set; } = "";

    [ObservableProperty]
    public partial string Status { get; private set; } = "請建立或開啟案件。";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCaseCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenSelectedCaseCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCaseCommand))]
    [NotifyCanExecuteChangedFor(nameof(CloseCaseCommand))]
    [NotifyCanExecuteChangedFor(nameof(ChangeCasesRootCommand))]
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
    [NotifyPropertyChangedFor(nameof(SelectedDetail))]
    public partial EventRowViewModel? SelectedEvent { get; set; }

    /// <summary>選取事件的詳細資訊：原始位置、穩定識別碼、旗標與完整訊息。</summary>
    public string SelectedDetail => SelectedEvent is not { Event: var e } row
        ? ""
        : $"來源：{EventRowViewModel.FormatSource(e.Source)}\n識別碼：{e.Id}\n原始時間：{e.RawTimestamp}"
          + (row.Flags.Count == 0 ? "" : "\n旗標：" + string.Join("、", row.Flags.Select(f => $"{f.Glyph} {f.Name}")))
          + $"\n\n{EventRowViewModel.DisplayText(e.Message)}";

    partial void OnSelectedEventChanged(EventRowViewModel? value) => UpdateRawLog(value);

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
                var vm = new RawLineViewModel(line.LineNo, EventRowViewModel.DisplayText(line.Text),
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

    bool CanCreateCase() => !IsBusy && !string.IsNullOrWhiteSpace(NewCaseName);

    [RelayCommand(CanExecute = nameof(CanCreateCase))]
    void CreateCase()
    {
        try
        {
            CloseCurrent();
            var c = _library.Create(NewCaseName, PlantConfig.Default);
            SetCurrent(c);
            Status = $"已建立案件「{c.Info.Name}」。請選擇要匯入的 LOG（資料夾、7z 或 zip）。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
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
        CurrentCaseName = c.Info.Name;
        CurrentCaseFolder = c.Folder;
        SaveSettings(_settings.WithRecent(c.Folder));
        RefreshCases();
        SelectedCase = Cases.FirstOrDefault(i => string.Equals(i.Folder, c.Folder, StringComparison.OrdinalIgnoreCase));
    }

    void CloseCurrent()
    {
        SelectedEvent = null;
        Events.Clear();
        Modules.Clear();
        _case?.Dispose();
        _case = null;
        CurrentCaseName = null;
        CurrentCaseFolder = null;
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
        var (modules, rows) = await Task.Run(() => LoadAll(c));
        SelectedEvent = null;
        Events.Clear();
        Modules.Clear();
        foreach (var m in modules) Modules.Add(m);
        foreach (var r in rows) Events.Add(r);
        SelectedEvent = Events.FirstOrDefault();
    }

    static (List<string> Modules, List<EventRowViewModel> Rows) LoadAll(Case c)
    {
        var order = c.Info.ModuleOrder.ToList();
        var modules = c.GetFiles().Where(f => f.Active).Select(f => f.Module).Distinct(StringComparer.Ordinal)
            .OrderBy(m => order.Contains(m) ? order.IndexOf(m) : order.Count).ThenBy(m => m, StringComparer.Ordinal)
            .ToList();
        var rows = new List<EventRowViewModel>((int)Math.Min(c.EventCount, int.MaxValue));
        long next = 1;
        while (true)
        {
            var page = c.GetEvents(next, PageSize);
            if (page.Count == 0) break;
            rows.AddRange(page.Select(e => new EventRowViewModel(e, modules.IndexOf(e.Module))));
            next = page[^1].Sequence + 1;
        }
        return (modules, rows);
    }

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
