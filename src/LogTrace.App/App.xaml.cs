using System.Windows;
using LogTrace.Engine.Cases;
using LogTrace.Workspace;

namespace LogTrace.App;

public partial class App : Application
{
    MainViewModel? _vm;

    /// <summary>命令列參數：--cases-root &lt;資料夾&gt; 指定案件存放位置（預設 %LOCALAPPDATA%\LogTrace\Cases）。</summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var root = CaseLibrary.DefaultRoot;
        for (int i = 0; i + 1 < e.Args.Length; i++)
            if (e.Args[i] == "--cases-root") root = e.Args[i + 1];

        _vm = new MainViewModel(new CaseLibrary(root), new WpfFolderPicker());
        new MainWindow { DataContext = _vm }.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _vm?.Dispose();
        base.OnExit(e);
    }
}

sealed class WpfFolderPicker : IFolderPicker
{
    public string? PickFolder(string title)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = title };
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }
}
