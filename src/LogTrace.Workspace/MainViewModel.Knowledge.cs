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
    SpecTables _specs = SpecTables.Empty;

    /// <summary>
    /// 對照表的資料夾（ADR-0021）：廠別設定資料夾的上一層加上 Knowledge（預設 D:\LogTrace\Knowledge）。
    /// 只放在本機，不進 repo。
    /// </summary>
    string KnowledgeRoot => Path.Combine(Path.GetDirectoryName(_plantsRoot) ?? _plantsRoot, "Knowledge");

    /// <summary>知識庫面板的關鍵字（空白分隔，每個都要包含）。</summary>
    [ObservableProperty]
    public partial string KnowledgeQuery { get; set; } = "";

    /// <summary>知識庫面板列出的項目：沒有關鍵字時為全部。</summary>
    public ObservableCollection<KnowledgeEntry> KnowledgeResults { get; } = [];

    /// <summary>格式有誤、沒有使用的列：「第 N 行：原因」。</summary>
    public ObservableCollection<string> KnowledgeIssues { get; } = [];

    /// <summary>KnowledgeIssues 一行一項，給畫面上可以選取、複製的文字框（回報行號與原因用）。</summary>
    public string KnowledgeIssuesText => string.Join(Environment.NewLine, KnowledgeIssues);

    /// <summary>知識庫的狀態：檔案位置與筆數、沒有知識庫、搜尋結果的筆數。</summary>
    [ObservableProperty]
    public partial string KnowledgeStatus { get; private set; } = "請開啟案件。";

    IEmbedder? _embedder;

    /// <summary>
    /// 向量搜尋用的 embedding（ADR-0022，#171）：程式啟動時設為本機 Ollama 的 bge-m3；null 時不做向量搜尋（測試預設）。
    /// 異常紀錄面板共用同一個。
    /// </summary>
    public IEmbedder? Embedder
    {
        get => _embedder;
        set { _embedder = value; Incidents.Embedder = value; }
    }

    /// <summary>「意思相近（向量）」：關鍵字搜尋時另外列出的前幾名知識庫條目（只用來找候選，顯示原文與出處）。</summary>
    public ObservableCollection<KnowledgeEntry> KnowledgeSimilar { get; } = [];

    /// <summary>向量搜尋的狀態：處理中、前幾名、無法使用的原因；沒有關鍵字或沒有設定 embedding 時為空字串。</summary>
    [ObservableProperty]
    public partial string KnowledgeSimilarStatus { get; private set; } = "";

    int _similarRun;

    bool HasCase() => _case is not null;

    /// <summary>重新讀取知識庫檔（修改檔案後使用）；詳細資訊同時更新。</summary>
    [RelayCommand(CanExecute = nameof(HasCase))]
    void ReloadKnowledge()
    {
        LoadKnowledge();
        OnPropertyChanged(nameof(SelectedDetail));
        OnPropertyChanged(nameof(SelectedDetailSegments));
    }

    /// <summary>關鍵字搜尋（立即顯示），接著以向量搜尋列出意思相近的前幾名（ADR-0022，#171）。</summary>
    [RelayCommand(CanExecute = nameof(HasCase))]
    async Task SearchKnowledge()
    {
        ShowKnowledge(_knowledge?.Search(KnowledgeQuery) ?? [], _specs.Search(KnowledgeQuery));
        var total = (_knowledge?.Entries.Count ?? 0) + _specs.Entries.Count;
        if (total > 0 && !string.IsNullOrWhiteSpace(KnowledgeQuery))
            KnowledgeStatus = $"符合 {KnowledgeResults.Count:N0} 筆（共 {total:N0} 筆）";
        else UpdateKnowledgeStatus();
        await FindSimilarKnowledgeAsync();
    }

    /// <summary>
    /// 向量搜尋知識庫條目（不含對照表，2026-11-17 使用者決定另開）。向量檔放在知識庫檔旁（.kb.vec），第一次搜尋時建立，
    /// 之後只重算改過的條目。案件在搜尋中切換時，結果不顯示。
    /// </summary>
    async Task FindSimilarKnowledgeAsync()
    {
        var run = ++_similarRun;
        KnowledgeSimilar.Clear();
        KnowledgeSimilarStatus = "";
        if (Embedder is not { } embedder || string.IsNullOrWhiteSpace(KnowledgeQuery)) return;
        if (_knowledge is not { Entries.Count: > 0 } kb || _knowledgePath is null) return;
        KnowledgeSimilarStatus = "意思相近（向量）：處理中…（第一次搜尋要建立向量，條目多時需要一段時間）";
        var entries = kb.Entries;
        var result = await SimilarSearch.FindAsync(embedder, KnowledgeQuery, entries.Select(SimilarSearch.KnowledgeText).ToList(),
            SimilarSearch.KnowledgeVectorPath(_knowledgePath));
        if (run != _similarRun || !ReferenceEquals(kb, _knowledge)) return;
        if (result.Unavailable is { } reason)
        {
            KnowledgeSimilarStatus = $"意思相近（向量）無法使用：{reason}；關鍵字搜尋照常可用";
            return;
        }
        foreach (var i in result.Indices) KnowledgeSimilar.Add(entries[i]);
        KnowledgeSimilarStatus = $"意思相近（向量）前 {KnowledgeSimilar.Count} 筆：只是候選，請看原文判斷"
            + (result.Embedded > 0 ? $"（這次建立 {result.Embedded:N0} 筆向量）" : "");
    }

    void LoadKnowledge()
    {
        _knowledge = null;
        _knowledgePath = null;
        _specs = SpecTables.Empty;
        KnowledgeIssues.Clear();
        if (_case is { } c)
        {
            var plantPath = KnowledgeBase.PlantConfigPathFor(c.Info.Plant.Name, c.PlantSnapshotSource, new PlantConfigLibrary(_plantsRoot));
            _knowledgePath = plantPath is null ? null : KnowledgeBase.PathBeside(plantPath);
            Incidents.Open(plantPath, c.Info.Plant.Name, c.Info.Name); // 異常紀錄與知識庫放在同一個廠別設定檔旁邊（ADR-0016 第 4 點）
            try
            {
                _knowledge = _knowledgePath is null ? null : KnowledgeBase.Load(_knowledgePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                KnowledgeIssues.Add($"無法讀取：{ex.Message}");
            }
            foreach (var issue in _knowledge?.Issues ?? []) KnowledgeIssues.Add($"第 {issue.Line} 行：{issue.Reason}");
            // 對照表（ADR-0021）：共用的依設備種類，各廠補充的資料夾名稱為廠別設定檔名
            try
            {
                _specs = SpecTables.Load(KnowledgeRoot, plantPath is null ? null : Path.GetFileNameWithoutExtension(plantPath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                KnowledgeIssues.Add($"對照表無法讀取：{ex.Message}");
            }
            foreach (var issue in _specs.Issues)
                KnowledgeIssues.Add($"{(issue.Warning ? "注意：" : "")}{issue.File} 第 {issue.Line} 行：{issue.Reason}");
        }
        else Incidents.Close();
        OnPropertyChanged(nameof(KnowledgeIssuesText));
        ShowKnowledge(_knowledge?.Search(KnowledgeQuery) ?? [], _specs.Search(KnowledgeQuery));
        UpdateKnowledgeStatus();
        _similarRun++;
        KnowledgeSimilar.Clear();
        KnowledgeSimilarStatus = "";
        ReloadKnowledgeCommand.NotifyCanExecuteChanged();
        SearchKnowledgeCommand.NotifyCanExecuteChanged();
    }

    /// <summary>面板列出知識庫與對照表；對照表的列顯示成「[設備種類] 代碼」，原文、翻譯、類別與狀態、出處與版本。</summary>
    void ShowKnowledge(IEnumerable<KnowledgeEntry> entries, IEnumerable<SpecEntry> specs)
    {
        KnowledgeResults.Clear();
        foreach (var e in entries) KnowledgeResults.Add(e);
        foreach (var s in specs)
            KnowledgeResults.Add(new KnowledgeEntry(s.Line, $"[{s.Device}] {s.Code}",
                s.Original + (s.IsTranslated ? $"（翻譯：{s.Chinese}）" : ""),
                string.Join("；", new[] { s.Category, s.Status }.Where(x => x.Length > 0)),
                s.Source + (s.Version.Length == 0 ? "" : $"（規格書 {s.Version}）")));
    }

    void UpdateKnowledgeStatus() => KnowledgeStatus = (_case, _knowledge, _knowledgePath) switch
    {
        (null, _, _) => "請開啟案件。",
        (_, null, null) => $"{NoKnowledge}（找不到廠別「{_case.Info.Plant.Name}」的設定檔）",
        (_, null, var path) => $"{NoKnowledge}：可以在 {path} 建立（欄位：代碼,說明,處置,出處）",
        (_, var kb, var path) => $"知識庫 {kb.Entries.Count:N0} 筆（{path}）"
            + (kb.Issues.Count == 0 ? "" : $"；{kb.Issues.Count:N0} 列格式有誤，沒有使用"),
    } + (_case is null ? "" : _specs.Files.Count == 0 ? "；沒有對照表" : $"\n{_specs.Summary}"); // 沒有對照表時不多佔一行（ADR-0021）

    /// <summary>詳細資訊中的知識庫段落：事件每個警報代碼對到的說明、處置與出處；沒有警報代碼時為空字串。</summary>
    string KnowledgeDetail()
    {
        var codes = _selectedFields.Where(f => f.Kind == FieldKinds.AlarmCode).Select(f => f.Value).Distinct().ToList();
        if (codes.Count == 0) return "";
        var text = _knowledge is null ? $"\n知識庫：{NoKnowledge}" : "";
        var devices = EventDevices();
        foreach (var code in codes)
        {
            if (_knowledge is not null)
            {
                var entries = _knowledge.Lookup(code);
                text += $"\n知識庫（警報代碼 {code}）：" + (entries.Count == 0 ? "知識庫中沒有這個代碼" : "");
                foreach (var e in entries)
                    text += $"\n  說明：{e.Description}"
                        + (e.Action.Length == 0 ? "" : $"\n  處置：{e.Action}")
                        + (e.Source.Length == 0 ? "" : $"\n  出處：{e.Source}");
            }
            // 對照表（ADR-0021 第 5 點）：依事件對應到的設備種類；對應不到時列出所有種類
            if (_specs.Files.Count == 0) continue;
            var specs = _specs.Lookup(code, devices);
            if (specs.Count == 0) text += $"\n對照表（代碼 {code}）：對照表中沒有這個代碼" + (devices.Count > 0 ? $"（{string.Join("、", devices)}）" : "");
            foreach (var s in specs)
                text += $"\n對照表（{s.Device}{(s.Category.Length > 0 ? " " + s.Category : "")} {s.Code}）：{s.Original}"
                    + (s.IsTranslated ? $"\n  中文（翻譯）：{s.Chinese}" : "")
                    + $"\n  出處：{s.Source}" + (s.Version.Length == 0 ? "" : $"（規格書 {s.Version}）")
                    + (s.IsReviewed ? "" : "\n  狀態：待確認（尚未審核，不當作確定的說明）");
        }
        return text;
    }

    /// <summary>事件對應到的設備種類（ADR-0021 第 5 點）：RM 元素為 RM，搬送機為 STV；對應不到時為空（不限）。</summary>
    IReadOnlyList<string> EventDevices()
    {
        var model = Playback.Model;
        if (model is null) return [];
        return _selectedFields.Where(f => FieldKinds.Identifiers.Contains(f.Kind))
            .Select(f => model.Resolve(f.Value)?.Type switch { LayoutElementType.Rm => "RM", LayoutElementType.Vehicle => "STV", _ => null })
            .OfType<string>().Distinct().ToList();
    }

    void ClearKnowledge()
    {
        KnowledgeQuery = "";
        LoadKnowledge();
    }
}
