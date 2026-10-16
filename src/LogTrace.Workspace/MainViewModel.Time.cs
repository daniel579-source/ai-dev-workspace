using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogTrace.Engine.Events;
using LogTrace.Engine.Plants;

namespace LogTrace.Workspace;

/// <summary>時間偏移量對話框中的一個模組。</summary>
public sealed partial class ModuleOffsetViewModel(string module, string offset) : ObservableObject
{
    public string Module { get; } = module;

    /// <summary>額外偏移量（秒，可帶正負號與小數）；空白為 0。</summary>
    [ObservableProperty]
    public partial string Offset { get; set; } = offset;
}

/// <summary>
/// 時間偏移量對話框（ADR-0003 第 5 點）：案件整體偏移量，加上個別模組的額外偏移量，單位為秒、精度 100 微秒。
/// </summary>
public sealed partial class TimeCorrectionViewModel : ObservableObject
{
    public TimeCorrectionViewModel(TimeCorrection current, IEnumerable<string> modules)
    {
        CaseOffset = Text(current.CaseOffset);
        foreach (var m in modules.Concat(current.ModuleOffsets.Keys).Distinct(StringComparer.Ordinal))
            Modules.Add(new ModuleOffsetViewModel(m, current.ModuleOffsets.TryGetValue(m, out var o) ? Text(o) : ""));
    }

    static string Text(TimeSpan offset) => offset == TimeSpan.Zero ? ""
        : offset.TotalSeconds.ToString("+0.####;-0.####", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>整體偏移量（秒）；空白為 0。</summary>
    [ObservableProperty]
    public partial string CaseOffset { get; set; }

    public ObservableCollection<ModuleOffsetViewModel> Modules { get; } = [];

    /// <summary>輸入有誤時的說明。</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>確認後的偏移量。</summary>
    public TimeCorrection? Result { get; private set; }

    /// <summary>檢查輸入；正確時設定 Result 並回傳 true（對話框才關閉）。</summary>
    public bool TryAccept()
    {
        var problems = new List<string>();
        var caseOffset = TimeCorrection.ParseSeconds(CaseOffset);
        if (caseOffset is null) problems.Add("整體偏移量");
        var modules = new List<KeyValuePair<string, TimeSpan>>();
        foreach (var m in Modules)
        {
            if (TimeCorrection.ParseSeconds(m.Offset) is { } o) modules.Add(KeyValuePair.Create(m.Module, o));
            else problems.Add(m.Module);
        }
        if (problems.Count > 0)
        {
            Error = $"無法使用的偏移量：{string.Join("、", problems)}。請輸入秒數（例如 5、-1.25），精度 0.0001 秒，不超過正負一天。";
            return false;
        }
        Result = TimeCorrection.Create(caseOffset!.Value, modules);
        Error = null;
        return true;
    }

    /// <summary>全部改回 0。</summary>
    [RelayCommand]
    void Reset()
    {
        CaseOffset = "";
        foreach (var m in Modules) m.Offset = "";
    }
}

/// <summary>時區與時間偏移量（ADR-0003 第 1、5 點、ADR-0008 第 4 點：畫面頂端顯示時區，校正時明顯標示）。</summary>
public sealed partial class MainViewModel
{
    /// <summary>畫面頂端的時區。</summary>
    [ObservableProperty]
    public partial string TimeZoneText { get; private set; } = "時區：尚未開啟案件";

    /// <summary>案件有時間偏移量時的標示；沒有時為 null。</summary>
    [ObservableProperty]
    public partial string? CorrectionText { get; private set; }

    void UpdateTimeTexts()
    {
        TimeZoneText = _case is { } c ? "時區：" + PlantConfig.TimeZoneLabel(c.Info.Plant.TimeZone) : "時區：尚未開啟案件";
        CorrectionText = _case is { Info.Correction: { IsZero: false } tc } ? $"已校正（{tc.Describe()}）" : null;
    }

    bool CanEditTimeCorrection() => !IsBusy && _case is not null;

    /// <summary>
    /// 設定時間偏移量：以新的偏移量重新排序（模組偏移量會改變跨模組的先後），寫入稽核紀錄；
    /// 之後回到原本選取的事件（以穩定識別碼尋找）。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditTimeCorrection))]
    async Task EditTimeCorrectionAsync()
    {
        if (_case is not { } c) return;
        var editor = new TimeCorrectionViewModel(c.Info.Correction, Modules);
        if (!_ui.EditTimeCorrection(editor) || editor.Result is not { } correction || correction.Equals(c.Info.Correction)) return;
        var selected = SelectedEvent?.Event.Id;
        IsBusy = true;
        Status = "依新的時間偏移量重新排序中…";
        try
        {
            await Task.Run(() => c.SetTimeCorrection(correction));
            UpdateTimeTexts();
            await LoadEventsAsync(c);
            if (selected is { } id && _allRows.FirstOrDefault(r => r.Event.Id == id) is { } row) GoToEvent(row.Sequence);
            Status = $"已設定時間偏移量：{correction.Describe()}。事件已依校正後的時間重新排序；原始時間保留在詳細資訊中。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Status = "設定時間偏移量失敗：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
