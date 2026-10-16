using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using LogTrace.Engine.Import;

namespace LogTrace.App;

/// <summary>
/// 重複匯入時的內容衝突（ADR-0002 第 5 點）：逐檔選擇「改用新檔」或保留舊檔。
/// 兩種選擇下，舊檔與新檔都留在案件內；選擇會寫進稽核紀錄。
/// </summary>
sealed class ConflictDialog : Window
{
    readonly List<(ImportItem Item, CheckBox UseNew)> _rows = [];

    public IReadOnlySet<string> UseNewFor =>
        _rows.Where(r => r.UseNew.IsChecked == true).Select(r => r.Item.RelativePath).ToHashSet();

    public ConflictDialog(IReadOnlyList<ImportItem> conflicts)
    {
        Title = "重複匯入：內容衝突";
        Width = 760;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        AutomationProperties.SetAutomationId(this, "ConflictDialog");

        var panel = new DockPanel { Margin = new Thickness(12) };
        var intro = new TextBlock
        {
            Text = "下列檔案在案件中已經有同名的檔案，但內容不同（也不是「舊檔加上後半段」）。\n" +
                   "勾選「改用新檔」的檔案，之後的查詢改用新檔；沒有勾選的保留舊檔。兩種情況下，舊檔與新檔都會保留在案件內。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        };
        DockPanel.SetDock(intro, Dock.Top);
        panel.Children.Add(intro);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var apply = new Button { Content = "套用並匯入", Padding = new Thickness(12, 3, 12, 3), IsDefault = true };
        apply.Click += (_, _) => DialogResult = true;
        AutomationProperties.SetAutomationId(apply, "ApplyConflicts");
        var cancel = new Button { Content = "取消匯入", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        AutomationProperties.SetAutomationId(cancel, "CancelImport");
        buttons.Children.Add(apply);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);

        var list = new StackPanel();
        foreach (var item in conflicts)
        {
            var check = new CheckBox { Content = "改用新檔", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            var text = new TextBlock
            {
                Text = $"{item.RelativePath}\n舊檔 {Size(item.Existing?.Size ?? 0)}（第 {item.Existing?.Version} 版），新檔 {Size(item.Size)}",
                TextWrapping = TextWrapping.Wrap,
            };
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(check, Dock.Left);
            row.Children.Add(check);
            row.Children.Add(text);
            list.Children.Add(row);
            _rows.Add((item, check));
        }
        panel.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = panel;
    }

    static string Size(long bytes) => bytes >= 1024 * 1024
        ? (bytes / 1024d / 1024d).ToString("N1", CultureInfo.CurrentCulture) + " MB"
        : (bytes / 1024d).ToString("N1", CultureInfo.CurrentCulture) + " KB";
}
