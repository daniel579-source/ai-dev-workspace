using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogTrace.Engine.Cases;
using LogTrace.Engine.Flows;
using LogTrace.Engine.Knowledge;
using LogTrace.Engine.Playback;
using LogTrace.Engine.Plants;

namespace LogTrace.Workspace;

/// <summary>
/// 從正常命令學習的卡住比對（ADR-0024，#192）：匯入後自動把正常完成的命令段落計入廠別的樣板
/// （<c>&lt;廠別設定檔名&gt;.flows.json</c>，放在廠別設定檔旁）；卡住事件的詳細資訊顯示比對結果；
/// 知識庫面板的「收集進度」頁列出每種段落的狀態，並可以匯出、匯入樣板。不需要 LLM。
/// </summary>
public sealed partial class MainViewModel
{
    FlowCase? _flowCase;
    IReadOnlyList<Stall> _stalls = [];
    string? _flowPath;
    string? _flowStamp;
    FlowFile? _flows;
    /// <summary>本機的廠別設定（分類與角色一律以它判斷，ADR-0024 第 11 點）；找不到時用案件的快照。</summary>
    PlantConfigDocument? _flowPlant;
    readonly Dictionary<Stall, FlowFinding?> _findings = [];

    /// <summary>收集進度：每種段落一列。</summary>
    public ObservableCollection<FlowKindProgress> FlowProgress { get; } = [];

    /// <summary>收集進度的狀態：樣板檔位置與樣本數、對應不到 Layout 的段落，或無法使用的原因。</summary>
    [ObservableProperty]
    public partial string FlowStatus { get; private set; } = "請開啟案件。";

    LayoutSection? FlowLayout => _flowPlant?.Layout;

    int FlowRevision => _flowPlant?.Revision ?? _case?.Info.Plant.Revision ?? 0;

    /// <summary>讀取樣板檔並更新收集進度（開啟、關閉案件與重新載入知識庫時）。</summary>
    void LoadFlows()
    {
        _flows = null;
        _flowStamp = null;
        _flowPath = null;
        _flowPlant = null;
        _findings.Clear();
        if (_case is { } c)
        {
            var plantPath = KnowledgeBase.PlantConfigPathFor(c.Info.Plant.Name, c.PlantSnapshotSource, new PlantConfigLibrary(_plantsRoot));
            _flowPath = plantPath is null ? null : FlowStore.PathBeside(plantPath);
            try
            {
                _flowPlant = plantPath is null ? FindPlantDocument(c) : PlantConfigFile.Load(plantPath);
                if (_flowPath is not null) (_flows, _flowStamp) = FlowStore.Load(_flowPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or PlantConfigFormatException)
            {
                ShowFlowProgress();
                FlowStatus = "正常流程樣板無法讀取：" + ex.Message;
                return;
            }
        }
        ShowFlowProgress();
        ExportFlowsCommand.NotifyCanExecuteChanged();
        ImportFlowsCommand.NotifyCanExecuteChanged();
    }

    void ShowFlowProgress()
    {
        FlowProgress.Clear();
        if (_case is null)
        {
            FlowStatus = "請開啟案件。";
            return;
        }
        if (_flowPath is null || FlowLayout is null)
        {
            FlowStatus = $"找不到廠別「{_case.Info.Plant.Name}」的設定檔，無法收集正常流程。";
            return;
        }
        var (kinds, unmapped, reasons) = NormalFlows.Progress(_flows, FlowLayout);
        foreach (var k in kinds) FlowProgress.Add(k);
        FlowStatus = _flows is null
            ? $"還沒有樣本：匯入 LOG 後，正常完成的命令段落會自動計入（{_flowPath}）。"
            : $"樣本 {_flows.Samples.Count:N0} 筆，{kinds.Count} 種段落，可以判斷 {kinds.Count(k => k.Status == Engine.Flows.FlowStatus.Ready)} 種（{_flowPath}）"
              + (unmapped == 0 ? "" : $"\n對應不到 Layout 的段落 {unmapped:N0} 段，請在 Layout 補上別名：{string.Join("；", reasons.Take(5))}");
    }

    /// <summary>匯入後：把案件中正常完成的命令段落計入樣板（#192 使用者決定只在匯入後計入）。傳回附加在狀態列的說明。</summary>
    async Task<string> CollectFlowsAsync()
    {
        if (_flowCase is not { } fc || _flowPath is not { } path || FlowLayout is not { } layout || _case is not { } c) return "";
        var (plant, revision, file, stamp, now) = (c.Info.Plant.Name, FlowRevision, _flows, _flowStamp, DateTimeOffset.Now);
        try
        {
            var (updated, added, newStamp) = await Task.Run(() =>
            {
                var samples = NormalFlows.CollectNormal(fc, layout, revision, now);
                var (f, a, _) = NormalFlows.Add(file, plant, revision, layout, samples, now);
                return (f, a, a == 0 && file is not null ? stamp : FlowStore.Save(path, f, stamp));
            });
            (_flows, _flowStamp) = (updated, newStamp);
            _findings.Clear();
            ShowFlowProgress();
            return added == 0 ? "" : $"正常流程樣本新增 {added:N0} 筆。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FlowConflictException)
        {
            return "正常流程樣本沒有計入：" + ex.Message;
        }
    }

    bool CanUseFlows() => _flowPath is not null && FlowLayout is not null && !IsBusy;

    /// <summary>匯出樣板成一個檔案（給另一台經授權的工程師電腦匯入）。</summary>
    [RelayCommand(CanExecute = nameof(CanUseFlows))]
    void ExportFlows()
    {
        if (_flows is null)
        {
            FlowStatus = "還沒有樣本，沒有可以匯出的內容。";
            return;
        }
        if (_ui.PickFlowSavePath(Path.GetDirectoryName(_flowPath!)!, Path.GetFileName(_flowPath!)) is not { } target) return;
        try
        {
            FlowStore.Export(target, _flows);
            Status = $"已匯出正常流程樣板：{target}（樣本 {_flows.Samples.Count:N0} 筆）。這是廠別專屬資料，只在經授權的電腦之間傳遞。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "匯出失敗：" + ex.Message;
        }
    }

    /// <summary>匯入另一台電腦的樣板：先顯示新增、重複與狀態變化，確認後合併（ADR-0024 第 11 點）。</summary>
    [RelayCommand(CanExecute = nameof(CanUseFlows))]
    void ImportFlows()
    {
        if (_ui.PickFlowFile(Path.GetDirectoryName(_flowPath!)!) is not { } source) return;
        try
        {
            var incoming = FlowStore.Parse(File.ReadAllBytes(source));
            var preview = NormalFlows.PreviewImport(_flows, incoming, _case!.Info.Plant.Name, FlowRevision, FlowLayout!, DateTimeOffset.Now);
            if (preview.Error is { } error)
            {
                Status = "樣板沒有匯入：" + error;
                return;
            }
            var message = $"新增 {preview.Added:N0} 筆，重複 {preview.Duplicates:N0} 筆（以來源 LOG 雜湊去除）。"
                + (preview.RevisionNotice is { } n ? "\n\n" + n : "")
                + (preview.StatusChanges.Count == 0 ? "\n\n段落的狀態沒有改變。" : "\n\n狀態會改變的段落：\n" + string.Join("\n", preview.StatusChanges))
                + "\n\n要合併到本機的樣板嗎？";
            if (!_ui.Confirm("匯入正常流程樣板", message)) return;
            _flowStamp = FlowStore.Save(_flowPath!, preview.Merged!, _flowStamp);
            _flows = preview.Merged;
            _findings.Clear();
            ShowFlowProgress();
            OnPropertyChanged(nameof(SelectedDetail));
            OnPropertyChanged(nameof(SelectedDetailSegments));
            Status = $"已匯入正常流程樣板：新增 {preview.Added:N0} 筆，重複 {preview.Duplicates:N0} 筆。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or FlowConflictException)
        {
            Status = "樣板沒有匯入：" + ex.Message;
        }
    }

    /// <summary>
    /// 詳細資訊中的正常流程比對：選取的事件是某段命令卡住的最後一步，或目前時刻在卡住的時段中。
    /// 一律寫成差異，不寫成真因（ADR-0024 第 7 點）。
    /// </summary>
    string FlowDetail()
    {
        if (SelectedEvent is not { Event: var e } || _flowCase is not { } fc || FlowLayout is not { } layout) return "";
        var text = "";
        foreach (var stall in _stalls.Where(s => s.Command is not null && (s.LastSequence == e.Sequence || s.IsActiveAt(e.Timestamp))))
        {
            if (!_findings.TryGetValue(stall, out var finding)) _findings[stall] = finding = NormalFlows.Analyze(stall, fc, _flows, layout);
            if (finding is null) continue;
            text += $"\n正常流程比對（命令 {stall.Command}，{finding.KindText}）：{finding.Text}"
                + $"\n  出處：事件 {string.Join("、", finding.Sequences.Select(s => "#" + s))}";
        }
        return text;
    }
}
