using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogTrace.Engine.Incidents;

namespace LogTrace.Workspace.Incidents;

/// <summary>總結的一列：區分（警報代碼／設備／異常類型）、項目、筆數、處置（含次數）與紀錄編號。</summary>
public sealed record SummaryRowViewModel(string Section, string Key, int Count, string Disposals, IReadOnlyList<string> RecordIds);

/// <summary>
/// 「總結」分頁（ADR-0023 第 1 點，#168）：每廠一份，依目前有效的異常紀錄由程式統計，工程師寫一段說明，審核後才使用。
/// 檔案為廠別設定檔旁的 &lt;設定檔名&gt;.summary.json；只保留最新一份已審核的總結，再產生的草稿審核通過前舊的繼續有效。
/// 只給工程師看（目前程式沒有第一線的畫面）。
/// </summary>
public sealed partial class IncidentsViewModel
{
    string? _summaryPath;
    PlantSummaryFile? _summary;
    string? _summaryStamp;

    /// <summary>總結的狀態：有效總結的產生時間、距今多久、涵蓋筆數、之後新增幾筆；草稿；讀不了的原因。</summary>
    [ObservableProperty]
    public partial string SummaryStatus { get; private set; } = "請開啟案件。";

    /// <summary>勾選時顯示草稿，否則顯示目前有效（已審核）的總結。</summary>
    [ObservableProperty]
    public partial bool SummaryShowDraft { get; set; }

    partial void OnSummaryShowDraftChanged(bool value) => ShowSummary();

    public ObservableCollection<SummaryRowViewModel> SummaryRows { get; } = [];

    /// <summary>目前顯示的總結的說明（草稿可以修改，已審核的唯讀）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveSummaryNoteCommand))]
    public partial string SummaryNote { get; set; } = "";

    /// <summary>目前顯示的總結的資訊：產生者與時間、涵蓋筆數、審核者與時間、審核時的提醒。</summary>
    [ObservableProperty]
    public partial string SummaryInfo { get; private set; } = "";

    [ObservableProperty]
    public partial SummaryRowViewModel? SelectedSummaryRow { get; set; }

    partial void OnSelectedSummaryRowChanged(SummaryRowViewModel? value)
    {
        SummaryRecordIds.Clear();
        foreach (var id in value?.RecordIds ?? []) SummaryRecordIds.Add(id);
    }

    /// <summary>選取的列所引用的紀錄編號；點選時在右側顯示該紀錄（原文）。</summary>
    public ObservableCollection<string> SummaryRecordIds { get; } = [];

    [ObservableProperty]
    public partial string? SelectedSummaryRecordId { get; set; }

    partial void OnSelectedSummaryRecordIdChanged(string? value) { if (value is not null) Select(value); }

    public bool HasSummaryDraft => _summary?.Draft is not null;

    void OpenSummary(string? plantConfigPath) => _summaryPath = plantConfigPath is null ? null : PlantSummaryStore.PathBeside(plantConfigPath);

    void LoadSummary()
    {
        _summary = null;
        _summaryStamp = null;
        string? error = null;
        if (_summaryPath is not null)
        {
            try
            {
                (_summary, _summaryStamp) = PlantSummaryStore.Load(_summaryPath);
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
            {
                error = ex.Message;
            }
        }
        ShowSummary(error);
    }

    /// <summary>
    /// 顯示有效總結或草稿，並更新狀態（新增筆數依目前的紀錄計算）。statusOnly 時只更新狀態，
    /// 不重設清單與說明（修改異常紀錄時，正在寫的說明不會被蓋掉）。
    /// </summary>
    void ShowSummary(string? error = null, bool statusOnly = false)
    {
        var records = _records.Select(r => r.Record).ToList();
        var shown = SummaryShowDraft ? _summary?.Draft : _summary?.Reviewed;
        if (!statusOnly) ShowSummaryContent(shown);
        SummaryStatus = StatusText(records, error);
        OnPropertyChanged(nameof(HasSummaryDraft));
        GenerateSummaryCommand.NotifyCanExecuteChanged();
        SaveSummaryNoteCommand.NotifyCanExecuteChanged();
        ApproveSummaryCommand.NotifyCanExecuteChanged();
        DiscardSummaryDraftCommand.NotifyCanExecuteChanged();
    }

    void ShowSummaryContent(SummaryContent? shown)
    {
        SummaryRows.Clear();
        SummaryRecordIds.Clear();
        if (shown is not null)
        {
            void Add(string section, IEnumerable<SummaryItem> items)
            {
                foreach (var i in items)
                    SummaryRows.Add(new(section, i.Key, i.Count,
                        string.Join("；", i.Disposals.Select(d => d.Count > 1 ? $"{d.Text}（{d.Count} 筆）" : d.Text)), i.RecordIds));
            }
            Add("警報代碼", shown.ByAlarmCode);
            Add("設備", shown.ByDevice);
            Add("異常類型（推得）", shown.ByKind);
        }
        SummaryNote = shown?.Note ?? "";
        SummaryInfo = shown is null ? "" :
            $"產生：{shown.GeneratedBy} {shown.GeneratedAt:yyyy-MM-dd HH:mm}，涵蓋 {shown.Covered.Count:N0} 筆有效紀錄"
            + (!SummaryShowDraft && _summary?.Reviewer is { } who ? $"\n審核：{who} {_summary.ReviewedAt:yyyy-MM-dd HH:mm}"
                + (_summary.ReviewWarnings.Count == 0 ? "" : $"（審核時的提醒：{string.Join("；", _summary.ReviewWarnings)}）") : "")
            + (SummaryShowDraft ? "\n草稿：審核通過前不使用；可以修改說明" : "");
    }

    string StatusText(List<IncidentRecord> records, string? error) =>
        _summaryPath is null
            ? _caseName is null ? "請開啟案件。" : $"找不到廠別「{_plant}」的設定檔，無法使用總結。"
            : error is not null ? $"總結檔讀不了：{error}"
            : (_summary?.Reviewed is { } rv
                ? $"目前的總結：{rv.GeneratedAt:yyyy-MM-dd HH:mm} 產生（{PlantSummaries.AgeText(Clock() - rv.GeneratedAt)}前），涵蓋 {rv.Covered.Count:N0} 筆；"
                  + $"之後新增或改版的已審核紀錄 {PlantSummaries.NewSince(rv, records):N0} 筆"
                : "還沒有審核過的總結")
              + (_summary?.Draft is { } d ? $"；有一份草稿（{d.GeneratedAt:yyyy-MM-dd HH:mm} 產生，待審核）" : "");

    bool CanGenerateSummary() => _summaryPath is not null;

    /// <summary>產生總結（手動）：依目前有效的紀錄統計，成為新的草稿（取代原本的草稿）；說明預先帶入原本的說明。</summary>
    [RelayCommand(CanExecute = nameof(CanGenerateSummary))]
    void GenerateSummary()
    {
        if (_summary?.Draft is not null && !_confirm("產生總結", "已有一份總結草稿，重新產生會取代它（說明沿用）。要繼續嗎？")) return;
        if (WriteSummary(PlantSummaries.Generate(_summary, _plant, _records.Select(r => r.Record), UserName, Clock())))
        {
            SummaryShowDraft = true;
            ShowSummary();
            Status = "已產生總結草稿，審核通過後才使用。";
        }
    }

    bool CanEditSummaryDraft() => SummaryShowDraft && _summary?.Draft is not null;

    [RelayCommand(CanExecute = nameof(CanEditSummaryDraft))]
    void SaveSummaryNote()
    {
        if (WriteSummary(PlantSummaries.SaveNote(_summary!, SummaryNote))) Status = "已儲存總結的說明。";
    }

    /// <summary>審核通過：草稿取代目前的總結。提醒（說明空白、產生者與審核者同一人）確認後仍可通過；沒有填使用者時不能通過。</summary>
    [RelayCommand(CanExecute = nameof(CanEditSummaryDraft))]
    void ApproveSummary()
    {
        if (string.IsNullOrWhiteSpace(UserName))
        {
            Status = "請先填寫使用者（審核者）。";
            return;
        }
        var file = PlantSummaries.SaveNote(_summary!, SummaryNote);
        var warnings = PlantSummaries.Warnings(file, UserName);
        var message = "審核通過後，這份草稿取代目前的總結（只保留最新一份）。"
            + (warnings.Count == 0 ? "" : "\n提醒：" + string.Join("；", warnings));
        if (!_confirm("審核總結", message)) return;
        if (WriteSummary(PlantSummaries.Approve(file, UserName, Clock())))
        {
            SummaryShowDraft = false;
            ShowSummary();
            Status = "總結已審核通過。";
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditSummaryDraft))]
    void DiscardSummaryDraft()
    {
        if (!_confirm("捨棄總結草稿", "捨棄這份總結草稿？目前的總結不受影響。")) return;
        if (WriteSummary(PlantSummaries.DiscardDraft(_summary!)))
        {
            SummaryShowDraft = false;
            ShowSummary();
            Status = "已捨棄總結草稿。";
        }
    }

    /// <summary>存檔；檔案已被別人修改時不覆寫，提示重新載入。</summary>
    bool WriteSummary(PlantSummaryFile file)
    {
        try
        {
            _summaryStamp = PlantSummaryStore.Save(_summaryPath!, file, _summaryStamp);
            _summary = file;
            return true;
        }
        catch (PlantSummaryConflictException ex)
        {
            Status = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "總結存檔失敗：" + ex.Message;
        }
        return false;
    }
}
