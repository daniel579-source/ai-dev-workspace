using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogTrace.Engine.Knowledge;
using LogTrace.Engine.Parsing;
using LogTrace.Engine.Plants;

namespace LogTrace.Workspace;

/// <summary>
/// 知識庫第一版（ADR-0013 第 2 點）：開啟案件時依廠別讀取 <c>&lt;廠別設定檔名&gt;.kb.csv</c>。
/// 詳細資訊中，事件的警報代碼對到知識庫時顯示說明、處置與出處；「知識庫」面板以關鍵字搜尋。
/// 不使用 LLM、不存入案件快照、不記入稽核。
/// </summary>
public sealed partial class MainViewModel
{
    const string NoKnowledge = "這個廠別沒有知識庫";

    KnowledgeBase? _knowledge;
    string? _knowledgePath;

    /// <summary>知識庫面板的關鍵字（空白分隔，每個都要包含）。</summary>
    [ObservableProperty]
    public partial string KnowledgeQuery { get; set; } = "";

    /// <summary>知識庫面板列出的項目：沒有關鍵字時為全部。</summary>
    public ObservableCollection<KnowledgeEntry> KnowledgeResults { get; } = [];

    /// <summary>格式有誤、沒有使用的列：「第 N 行：原因」。</summary>
    public ObservableCollection<string> KnowledgeIssues { get; } = [];

    /// <summary>知識庫的狀態：檔案位置與筆數、沒有知識庫、搜尋結果的筆數。</summary>
    [ObservableProperty]
    public partial string KnowledgeStatus { get; private set; } = "請開啟案件。";

    bool HasCase() => _case is not null;

    /// <summary>重新讀取知識庫檔（修改檔案後使用）；詳細資訊同時更新。</summary>
    [RelayCommand(CanExecute = nameof(HasCase))]
    void ReloadKnowledge()
    {
        LoadKnowledge();
        OnPropertyChanged(nameof(SelectedDetail));
        OnPropertyChanged(nameof(SelectedDetailSegments));
    }

    [RelayCommand(CanExecute = nameof(HasCase))]
    void SearchKnowledge()
    {
        ShowKnowledge(_knowledge?.Search(KnowledgeQuery) ?? []);
        if (_knowledge is not null && !string.IsNullOrWhiteSpace(KnowledgeQuery))
            KnowledgeStatus = $"符合 {KnowledgeResults.Count:N0} 筆（共 {_knowledge.Entries.Count:N0} 筆）";
        else UpdateKnowledgeStatus();
    }

    void LoadKnowledge()
    {
        _knowledge = null;
        _knowledgePath = null;
        KnowledgeIssues.Clear();
        if (_case is { } c)
        {
            _knowledgePath = KnowledgeBase.PathFor(c.Info.Plant.Name, c.PlantSnapshotSource, new PlantConfigLibrary(_plantsRoot));
            try
            {
                _knowledge = _knowledgePath is null ? null : KnowledgeBase.Load(_knowledgePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                KnowledgeIssues.Add($"無法讀取：{ex.Message}");
            }
            foreach (var issue in _knowledge?.Issues ?? []) KnowledgeIssues.Add($"第 {issue.Line} 行：{issue.Reason}");
        }
        ShowKnowledge(_knowledge?.Search(KnowledgeQuery) ?? []);
        UpdateKnowledgeStatus();
        ReloadKnowledgeCommand.NotifyCanExecuteChanged();
        SearchKnowledgeCommand.NotifyCanExecuteChanged();
    }

    void ShowKnowledge(IEnumerable<KnowledgeEntry> entries)
    {
        KnowledgeResults.Clear();
        foreach (var e in entries) KnowledgeResults.Add(e);
    }

    void UpdateKnowledgeStatus() => KnowledgeStatus = (_case, _knowledge, _knowledgePath) switch
    {
        (null, _, _) => "請開啟案件。",
        (_, null, null) => $"{NoKnowledge}（找不到廠別「{_case.Info.Plant.Name}」的設定檔）",
        (_, null, var path) => $"{NoKnowledge}：可以在 {path} 建立（欄位：代碼,說明,處置,出處）",
        (_, var kb, var path) => $"知識庫 {kb.Entries.Count:N0} 筆（{path}）"
            + (kb.Issues.Count == 0 ? "" : $"；{kb.Issues.Count:N0} 列格式有誤，沒有使用"),
    };

    /// <summary>詳細資訊中的知識庫段落：事件每個警報代碼對到的說明、處置與出處；沒有警報代碼時為空字串。</summary>
    string KnowledgeDetail()
    {
        var codes = _selectedFields.Where(f => f.Kind == FieldKinds.AlarmCode).Select(f => f.Value).Distinct().ToList();
        if (codes.Count == 0) return "";
        if (_knowledge is null) return $"\n知識庫：{NoKnowledge}";
        var text = "";
        foreach (var code in codes)
        {
            var entries = _knowledge.Lookup(code);
            text += $"\n知識庫（警報代碼 {code}）：" + (entries.Count == 0 ? "知識庫中沒有這個代碼" : "");
            foreach (var e in entries)
                text += $"\n  說明：{e.Description}"
                    + (e.Action.Length == 0 ? "" : $"\n  處置：{e.Action}")
                    + (e.Source.Length == 0 ? "" : $"\n  出處：{e.Source}");
        }
        return text;
    }

    void ClearKnowledge()
    {
        KnowledgeQuery = "";
        LoadKnowledge();
    }
}
