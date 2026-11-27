namespace LogTrace.Update;

/// <summary>
/// 啟動程式替換自己（ADR-0030 第 3 點第 9 步，2026-11-27 使用者選 7b）：程式包在 app\launcher\LogTrace.exe 附上新版啟動程式。
/// 啟動程式開啟時，若預設啟動版本內附的啟動程式與自己內容不同，就把自己改名成 LogTrace.old.exe（Windows 允許改名執行中的 exe），
/// 再把新版複製上來；新版從下一次啟動開始生效。LogTrace.old.exe 在下一次啟動時刪除。
/// </summary>
public static class LauncherSelfUpdate
{
    public const string LauncherFolderName = "launcher";
    public const string OldName = "LogTrace.old.exe";

    /// <summary>版本資料夾內附的新版啟動程式。</summary>
    public static string BundledLauncher(ProgramFolder folder, Version version) =>
        Path.Combine(folder.AppFolder(version), LauncherFolderName, ProgramFolder.AppExeName);

    /// <summary>刪除上次替換留下的舊啟動程式；刪不掉（例如還在執行）時下次再試。</summary>
    public static void CleanupOld(string selfPath)
    {
        var old = Path.Combine(Path.GetDirectoryName(selfPath)!, OldName);
        try
        {
            if (File.Exists(old)) File.Delete(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>需要時替換；傳回是否替換了。失敗時把自己改回原名，不影響這次啟動。</summary>
    public static bool Apply(string selfPath, string bundledPath)
    {
        if (!File.Exists(bundledPath) || !File.Exists(selfPath) || SameContent(selfPath, bundledPath)) return false;
        var old = Path.Combine(Path.GetDirectoryName(selfPath)!, OldName);
        try
        {
            if (File.Exists(old)) File.Delete(old);
            File.Move(selfPath, old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        try
        {
            File.Copy(bundledPath, selfPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                if (File.Exists(selfPath)) File.Delete(selfPath);
                File.Move(old, selfPath);
            }
            catch (Exception ex2) when (ex2 is IOException or UnauthorizedAccessException)
            {
            }
            return false;
        }
    }

    static bool SameContent(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        if (fa.Length != fb.Length) return false;
        return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
    }
}
