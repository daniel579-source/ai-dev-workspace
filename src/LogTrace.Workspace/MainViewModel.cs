using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogTrace.Engine.Cases;
using LogTrace.Engine.Plants;

namespace LogTrace.Workspace;

/// <summary>
/// 主畫面：建立案件、匯入、事件清單與選取。
/// 「目前時刻」、搜尋、回放等狀態在後續 ticket 加入。
/// </summary>
public sealed partial class MainViewModel(CaseLibrary library, IFolderPicker folderPicker) : ObservableObject, IDisposable
{
    const int PageSize = 10_000;
    Case? _case;

    public ObservableCollection<EventRowViewModel> Events { get; } = [];

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

    /// <summary>選取事件的詳細資訊：原始位置、穩定識別碼與完整訊息。</summary>
    public string SelectedDetail => SelectedEvent is not { Event: var e }
        ? ""
        : $"來源：{EventRowViewModel.FormatSource(e.Source)}\n識別碼：{e.Id}\n原始時間：{e.RawTimestamp}\n\n{e.Message}";

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
            var rows = await Task.Run(() => LoadAll(c));
            Events.Clear();
            foreach (var r in rows) Events.Add(r);
            SelectedEvent = Events.FirstOrDefault();
            Status = $"已匯入 {result.FileCount} 個檔案、{result.EventCount:N0} 筆事件。";
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

    static List<EventRowViewModel> LoadAll(Case c)
    {
        var rows = new List<EventRowViewModel>((int)Math.Min(c.EventCount, int.MaxValue));
        long next = 1;
        while (true)
        {
            var page = c.GetEvents(next, PageSize);
            if (page.Count == 0) break;
            rows.AddRange(page.Select(e => new EventRowViewModel(e)));
            next = page[^1].Sequence + 1;
        }
        return rows;
    }

    [RelayCommand]
    void BrowseImportFolder()
    {
        if (folderPicker.PickFolder("選擇要匯入的 LOG 資料夾") is { } folder) ImportFolder = folder;
    }

    public void Dispose() => _case?.Dispose();
}
