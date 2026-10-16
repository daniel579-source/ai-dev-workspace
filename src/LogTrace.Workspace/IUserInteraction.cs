using LogTrace.Engine.Import;

namespace LogTrace.Workspace;

/// <summary>需要使用者操作的對話框與外部程式；由畫面層提供，測試時可以替換。</summary>
public interface IUserInteraction
{
    /// <summary>選擇資料夾；取消時傳回 null。</summary>
    string? PickFolder(string title);

    /// <summary>選擇 7z 或 zip 壓縮檔；取消時傳回 null。</summary>
    string? PickArchive(string title);

    /// <summary>要求確認（例如刪除案件）；確定時傳回 true。</summary>
    bool Confirm(string title, string message);

    /// <summary>
    /// 重複匯入時有內容衝突的檔案（ADR-0002 第 5 點）：讓使用者逐檔選擇。
    /// 傳回要改用新檔的相對路徑（其餘保留舊檔）；取消匯入時傳回 null。
    /// </summary>
    IReadOnlySet<string>? ResolveConflicts(IReadOnlyList<ImportItem> conflicts);

    /// <summary>在檔案總管開啟資料夾。</summary>
    void OpenFolder(string folder);

    /// <summary>選擇要開啟的廠別設定檔（.json）；取消時傳回 null。</summary>
    string? PickPlantConfigFile(string initialFolder);

    /// <summary>選擇廠別設定檔的存檔位置；取消時傳回 null。</summary>
    string? PickPlantConfigSavePath(string initialFolder, string suggestedFileName);

    /// <summary>開啟 Layout 編輯器視窗。</summary>
    void ShowLayoutEditor(Layout.LayoutEditorViewModel editor);
}
