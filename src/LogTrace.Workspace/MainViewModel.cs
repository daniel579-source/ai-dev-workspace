using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogTrace.Engine.Cases;
using LogTrace.Engine.Plants;

namespace LogTrace.Workspace;

/// <summary>
/// 主畫面：建立案件、匯入、事件清單（混合／分軌檢視）與選取、原始 LOG 面板。
/// 「目前時刻」、搜尋、回放等狀態在後續 ticket 加入。
/// </summary>
public sealed partial class MainViewModel(CaseLibrary library, IFolderPicker folderPicker) : ObservableObject, IDisposable
{
    const int PageSize = 10_000;

    /// <summary>原始 LOG 面板顯示選取事件前後各幾行。</summary>
    public const int RawContextLines = 50;

    Case? _case;

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
    [NotifyCanExecuteChangedFor(nameof(CreateCaseCommand))]
    public partial string NewCaseName { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    public partial string ImportFolder { get; set; } = "";

    [ObservableProperty]
    public partial string? CurrentCaseName { get; private set; }

    [ObservableProperty]
    public partial string? CurrentCaseFolder { get; private set; }

    [ObservableProperty]
    public partial string Status { get; private set; } = "請先建立案件。";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCaseCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    public partial bool IsBusy { get; private set; }

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

    bool CanCreateCase() => !IsBusy && !string.IsNullOrWhiteSpace(NewCaseName);

    [RelayCommand(CanExecute = nameof(CanCreateCase))]
    void CreateCase()
    {
        try
        {
            _case?.Dispose();
            _case = library.Create(NewCaseName, PlantConfig.Default);
            CurrentCaseName = _case.Info.Name;
            CurrentCaseFolder = _case.Folder;
            Events.Clear();
            Modules.Clear();
            SelectedEvent = null;
            Status = $"已建立案件「{_case.Info.Name}」。請選擇要匯入的 LOG 資料夾。";
        }
        catch (Exception ex)
        {
            Status = "建立案件失敗：" + ex.Message;
        }
        ImportCommand.NotifyCanExecuteChanged();
    }

    bool CanImport() => !IsBusy && _case is not null && !string.IsNullOrWhiteSpace(ImportFolder);

    [RelayCommand(CanExecute = nameof(CanImport))]
    async Task ImportAsync()
    {
        var c = _case!;
        var folder = ImportFolder.Trim();
        IsBusy = true;
        Status = "匯入中…";
        try
        {
            var result = await Task.Run(() => c.Import(folder));
            var (modules, rows) = await Task.Run(() => LoadAll(c));
            Events.Clear();
            Modules.Clear();
            foreach (var m in modules) Modules.Add(m);
            foreach (var r in rows) Events.Add(r);
            SelectedEvent = Events.FirstOrDefault();
            Status = $"已匯入 {result.FileCount} 個檔案、{result.EventCount:N0} 筆事件。"
                + (result.UnparsableFileCount == 0 ? "" : $"無法解析的檔案 {result.UnparsableFileCount} 個（沒有產生事件）。");
        }
        catch (Exception ex)
        {
            Status = "匯入失敗：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    static (List<string> Modules, List<EventRowViewModel> Rows) LoadAll(Case c)
    {
        var order = c.Info.ModuleOrder;
        var modules = c.GetFiles().Select(f => f.Module).Distinct(StringComparer.Ordinal)
            .OrderBy(m => order.Contains(m) ? order.ToList().IndexOf(m) : order.Count).ThenBy(m => m, StringComparer.Ordinal)
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
        if (folderPicker.PickFolder("選擇要匯入的 LOG 資料夾") is { } folder) ImportFolder = folder;
    }

    public void Dispose() => _case?.Dispose();
}
