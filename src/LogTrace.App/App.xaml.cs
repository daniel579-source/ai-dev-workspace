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
    /// 程式設定與版面存放在 %LOCALAPPDATA%\LogTrace；案件與廠別設定預設存放在 D:\LogTrace（#57）。
    /// 命令列參數：--data-root &lt;資料夾&gt; 把設定、版面、預設的案件與廠別設定位置都改到這個資料夾，
    /// 不詢問搬移。冒煙測試用它隔離使用者的資料。
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string? testRoot = null;
        for (int i = 0; i + 1 < e.Args.Length; i++)
            if (e.Args[i] == "--data-root") testRoot = e.Args[i + 1];
        var settingsRoot = testRoot ?? CaseLibrary.AppDataRoot;
        var dataRoot = testRoot ?? CaseLibrary.DataRoot;

        var settings = new SettingsStore(System.IO.Path.Combine(settingsRoot, "settings.json"));
        _vm = new MainViewModel(settings, new WpfUserInteraction(), System.IO.Path.Combine(dataRoot, "Cases"),
            plantsRoot: System.IO.Path.Combine(dataRoot, "Plants"));
        var window = new MainWindow { DataContext = _vm, LayoutFile = System.IO.Path.Combine(settingsRoot, "layout.xml") };
        window.Show();
        // #57 之前的預設位置還有資料時，詢問是否搬到 D 槽
        if (testRoot is null)
            _vm.OfferRelocation(System.IO.Path.Combine(CaseLibrary.AppDataRoot, "Cases"), System.IO.Path.Combine(CaseLibrary.AppDataRoot, "Plants"));
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

    public bool EditTimeCorrection(TimeCorrectionViewModel editor) =>
        new TimeCorrectionDialog(editor) { Owner = Owner }.ShowDialog() == true;
}
