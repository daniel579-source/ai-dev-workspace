using System.Diagnostics;
using LogTrace.Update;

namespace LogTrace.Launcher;

/// <summary>
/// 啟動程式（ADR-0030）：
/// 1. 刪除上次替換留下的舊啟動程式。
/// 2. 掃描 updates\：無法套用的移到 rejected\ 並告知；等 Runtime 的程式包告知後留著。
/// 3. 有可以套用的升級包時詢問（MessageBox）；同意後顯示進度並解開，改成預設啟動的版本。
/// 4. 需要時替換自己（LogTrace.old.exe）。
/// 5. 啟動預設版本的主程式，命令列參數原樣傳過去。
/// </summary>
static class Program
{
    const string Title = "LogTrace";

    static int Main(string[] args)
    {
        var self = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, ProgramFolder.AppExeName);
        var folder = new ProgramFolder(AppContext.BaseDirectory);
        LauncherSelfUpdate.CleanupOld(self);

        try
        {
            var plan = UpdateRunner.Scan(folder);
            if (UpdateRunner.RejectAndDescribe(folder, plan) is { } notice) Win32.Info(Title, notice);
            var installable = UpdateRunner.Installable(plan);
            if (installable.Count > 0 && Win32.AskYesNo(Title, UpdateRunner.ConfirmText(installable, folder.CurrentApp())))
            {
                IReadOnlyList<string> errors = [];
                ProgressWindow.Run("LogTrace 升級", (status, progress) => errors = UpdateRunner.Apply(folder, installable, status, progress));
                if (errors.Count > 0)
                    Win32.Error(Title, "下列升級包套用失敗，已移到 updates\\rejected\\（旁邊有原因說明）：\r\n" + string.Join("\r\n", errors)
                        + "\r\n\r\n將以目前的版本啟動。");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Win32.Error(Title, "檢查升級包時發生錯誤，將以目前的版本啟動：\r\n" + ex.Message);
        }

        var current = folder.CurrentApp();
        if (current is null)
        {
            Win32.Error(Title, $"在 {folder.Root} 找不到可以啟動的 LogTrace 版本（app-<版本>\\）。\r\n請重新以完整的 zip 安裝。");
            return 1;
        }
        LauncherSelfUpdate.Apply(self, LauncherSelfUpdate.BundledLauncher(folder, current));

        try
        {
            var start = new ProcessStartInfo(folder.AppExe(current)) { UseShellExecute = false, WorkingDirectory = folder.AppFolder(current) };
            foreach (var a in args) start.ArgumentList.Add(a);
            Process.Start(start);
            return 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            Win32.Error(Title, $"無法啟動 {folder.AppExe(current)}：\r\n{ex.Message}\r\n\r\n可能被防毒軟體或公司的應用程式管控擋下，請洽 IT。");
            return 1;
        }
    }
}
