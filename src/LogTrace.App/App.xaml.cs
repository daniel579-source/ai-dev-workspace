using System.Diagnostics;
using System.Windows;
using LogTrace.Engine.Cases;
using LogTrace.Engine.Import;
using LogTrace.Workspace;

namespace LogTrace.App;

public partial class App : Application
{
    MainViewModel? _vm;

    /// <summary>
    /// 命令列參數：--data-root &lt;資料夾&gt; 指定程式的本機資料夾（設定檔與預設的案件存放位置；
    /// 預設 %LOCALAPPDATA%\LogTrace）。冒煙測試用它隔離使用者的設定。
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var dataRoot = CaseLibrary.AppDataRoot;
        for (int i = 0; i + 1 < e.Args.Length; i++)
            if (e.Args[i] == "--data-root") dataRoot = e.Args[i + 1];

        var settings = new SettingsStore(System.IO.Path.Combine(dataRoot, "settings.json"));
        _vm = new MainViewModel(settings, new WpfUserInteraction(), System.IO.Path.Combine(dataRoot, "Cases"));
        var window = new MainWindow { DataContext = _vm, LayoutFile = System.IO.Path.Combine(dataRoot, "layout.xml") };
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _vm?.Dispose();
        base.OnExit(e);
    }
}

sealed class WpfUserInteraction : IUserInteraction
{
    static Window? Owner => Application.Current.MainWindow;

    public string? PickFolder(string title)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = title };
        return dlg.ShowDialog(Owner) == true ? dlg.FolderName : null;
    }

    public string? PickArchive(string title)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = title,
            Filter = "LOG 壓縮檔 (*.7z;*.zip)|" + string.Join(";", ArchiveExtractor.Extensions.Select(x => "*" + x)),
        };
        return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
    }

    public bool Confirm(string title, string message) =>
        MessageBox.Show(Owner!, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    public IReadOnlySet<string>? ResolveConflicts(IReadOnlyList<ImportItem> conflicts)
    {
        var dlg = new ConflictDialog(conflicts) { Owner = Owner };
        return dlg.ShowDialog() == true ? dlg.UseNewFor : null;
    }

    public void OpenFolder(string folder) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = false });

    const string PlantConfigFilter = "廠別設定檔 (*.json)|*.json";

    public string? PickPlantConfigFile(string initialFolder)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "開啟廠別設定檔", Filter = PlantConfigFilter, InitialDirectory = initialFolder };
        return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
    }

    public string? PickPlantConfigSavePath(string initialFolder, string suggestedFileName)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "另存廠別設定檔", Filter = PlantConfigFilter, InitialDirectory = initialFolder, FileName = suggestedFileName,
        };
        return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
    }

    public void ShowLayoutEditor(LogTrace.Workspace.Layout.LayoutEditorViewModel editor) =>
        new LayoutEditorWindow(editor) { Owner = Owner }.Show();
}
