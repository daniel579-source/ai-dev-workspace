namespace LogTrace.Workspace;

/// <summary>選擇資料夾的對話框；由畫面層提供，測試時可以替換。</summary>
public interface IFolderPicker
{
    /// <summary>傳回選擇的資料夾；取消時傳回 null。</summary>
    string? PickFolder(string title);
}
