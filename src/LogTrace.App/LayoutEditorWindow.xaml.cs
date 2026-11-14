using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using LogTrace.Engine.Plants;
using LogTrace.Workspace.Layout;

namespace LogTrace.App;

/// <summary>Layout 編輯器視窗：3D 畫布（Layout3DEditorView）把滑鼠操作換算成公尺交給 ViewModel（吸附、最小尺寸等規則都在 ViewModel，ADR-0018）。</summary>
public partial class LayoutEditorWindow : Window
{
    public static IReadOnlyList<int> Rotations => LayoutEditorViewModel.Rotations;

    readonly LayoutEditorViewModel _vm;

    public LayoutEditorWindow(LayoutEditorViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        Closed += (_, _) => Editor3D.Shutdown();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_vm.ConfirmDiscard()) e.Cancel = true;
        base.OnClosing(e);
    }

    // ---- 鍵盤：Ctrl+C／Ctrl+V／Del／Ctrl+Z（ADR-0006 第 10 點）；在文字框中維持文字編輯的行為。
    //      3D 畫布有焦點時，按鍵由網頁轉給 Layout3DEditorView（WebView2 的按鍵不經過 WPF） ----

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBoxBase or PasswordBox or ComboBox { IsEditable: true }) return;
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        switch (e.Key)
        {
            case Key.C when ctrl:
                _vm.CopyCommand.Execute(null);
                break;
            case Key.V when ctrl:
                _vm.Paste(null, null); // 焦點不在 3D 畫布：放在原位置往右下 1 公尺
                break;
            case Key.Z when ctrl:
                if (_vm.UndoCommand.CanExecute(null)) _vm.UndoCommand.Execute(null);
                break;
            case Key.Delete when Keyboard.Modifiers == ModifierKeys.None:
                if (_vm.DeleteSelectedCommand.CanExecute(null)) _vm.DeleteSelectedCommand.Execute(null);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>回到編輯器時重新列出可以比對的案件。</summary>
    void Window_Activated(object? sender, EventArgs e) => _vm.RefreshCaseChoices();

    // ---- 未對應的編號（ADR-0006 第 3 點）：雙擊設為選取元素的別名（#117：WebView2 收不到 WPF 的拖放） ----

    void Unmapped_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.AssignSelectedUnmappedCommand.CanExecute(null)) _vm.AssignSelectedUnmappedCommand.Execute(null);
    }

    void ConfigList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ConfigList.SelectedItem is string path) _vm.OpenConfigCommand.Execute(path);
    }
}

/// <summary>元素種類的中文名稱。</summary>
public sealed class ElementTypeLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is LayoutElementType t ? LayoutElementTypes.Label(t) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>完整路徑只顯示檔名。</summary>
public sealed class FileNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string path ? System.IO.Path.GetFileNameWithoutExtension(path) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
