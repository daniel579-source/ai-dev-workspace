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
}
