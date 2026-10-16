using LogTrace.Engine.Import;

namespace LogTrace.Workspace.Tests;

/// <summary>測試用的對話框：預先設定回答，並記錄被呼叫的情況。</summary>
sealed class FakeUserInteraction : IUserInteraction
{
    public string? FolderResult { get; set; }
    public string? ArchiveResult { get; set; }
    public bool ConfirmResult { get; set; }
    public string? LastConfirmMessage { get; private set; }

    /// <summary>回答衝突對話框；null 表示取消匯入。</summary>
    public Func<IReadOnlyList<ImportItem>, IReadOnlySet<string>?> Conflicts { get; set; } = _ => new HashSet<string>();
    public IReadOnlyList<ImportItem>? LastConflicts { get; private set; }
    public List<string> OpenedFolders { get; } = [];

    public string? PickFolder(string title) => FolderResult;
    public string? PickArchive(string title) => ArchiveResult;

    public bool Confirm(string title, string message)
    {
        LastConfirmMessage = message;
        return ConfirmResult;
    }

    public IReadOnlySet<string>? ResolveConflicts(IReadOnlyList<ImportItem> conflicts)
    {
        LastConflicts = conflicts;
        return Conflicts(conflicts);
    }

    public void OpenFolder(string folder) => OpenedFolders.Add(folder);

    public string? PlantConfigFileResult { get; set; }
    public string? PlantConfigSavePathResult { get; set; }
    public LogTrace.Workspace.Layout.LayoutEditorViewModel? ShownEditor { get; private set; }

    public string? PickPlantConfigFile(string initialFolder) => PlantConfigFileResult;
    public string? PickPlantConfigSavePath(string initialFolder, string suggestedFileName) => PlantConfigSavePathResult;
    public void ShowLayoutEditor(LogTrace.Workspace.Layout.LayoutEditorViewModel editor) => ShownEditor = editor;

    /// <summary>填寫時間偏移量對話框；回傳 false 表示取消。預設取消。</summary>
    public Func<TimeCorrectionViewModel, bool> TimeCorrection { get; set; } = _ => false;

    public bool EditTimeCorrection(TimeCorrectionViewModel editor) => TimeCorrection(editor) && editor.TryAccept();
}
