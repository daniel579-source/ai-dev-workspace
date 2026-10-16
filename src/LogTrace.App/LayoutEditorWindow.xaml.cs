using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using LogTrace.Engine.Plants;
using LogTrace.Workspace.Layout;

namespace LogTrace.App;

/// <summary>Layout 編輯器視窗：畫布上的滑鼠操作轉成公尺後交給 ViewModel（吸附、最小尺寸等規則都在 ViewModel）。</summary>
public partial class LayoutEditorWindow : Window
{
    public static IReadOnlyList<int> Rotations => LayoutEditorViewModel.Rotations;

    readonly LayoutEditorViewModel _vm;
    Point? _dragStart;
    bool _resizing;

    public LayoutEditorWindow(LayoutEditorViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_vm.ConfirmDiscard()) e.Cancel = true;
        base.OnClosing(e);
    }

    void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var hit = e.OriginalSource as FrameworkElement;
        var element = FindElement(hit);
        if (element is null)
        {
            _vm.SelectedElement = null;
            return;
        }
        _vm.BeginDrag(element);
        _resizing = hit?.Tag is "resize";
        _dragStart = e.GetPosition(LayoutCanvas);
        LayoutCanvas.CaptureMouse();
        e.Handled = true;
    }

    void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start) return;
        var p = e.GetPosition(LayoutCanvas);
        double dx = (p.X - start.X) / MetersToPixelsConverter.PixelsPerMeter, dy = (p.Y - start.Y) / MetersToPixelsConverter.PixelsPerMeter;
        if (_resizing) _vm.DragResizeBy(dx, dy);
        else _vm.DragMoveBy(dx, dy);
    }

    void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is null) return;
        _dragStart = null;
        _vm.EndDrag();
        LayoutCanvas.ReleaseMouseCapture();
    }

    static LayoutElementViewModel? FindElement(FrameworkElement? fe)
    {
        for (var d = fe; d is not null; d = System.Windows.Media.VisualTreeHelper.GetParent(d) as FrameworkElement)
            if (d.Tag is LayoutElementViewModel vm) return vm;
        return null;
    }

    void ConfigList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ConfigList.SelectedItem is string path) _vm.OpenConfigCommand.Execute(path);
    }
}

/// <summary>公尺轉成畫布上的像素（1 公尺 = 32 像素）。</summary>
public sealed class MetersToPixelsConverter : IValueConverter
{
    public const double PixelsPerMeter = 32;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double m ? m * PixelsPerMeter : 0d;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double px ? px / PixelsPerMeter : 0d;
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
