using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using LogTrace.Workspace;

namespace LogTrace.App;

/// <summary>
/// 時間偏移量（ADR-0003 第 5 點）：案件整體偏移量，加上個別模組的額外偏移量（秒）。
/// 確定時檢查輸入，有誤時顯示說明、不關閉。
/// </summary>
sealed class TimeCorrectionDialog : Window
{
    public TimeCorrectionDialog(TimeCorrectionViewModel editor)
    {
        Title = "時間校正";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        DataContext = editor;
        AutomationProperties.SetAutomationId(this, "TimeCorrectionDialog");

        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock
        {
            Text = "顯示的時間＝LOG 記錄的時間＋整體偏移量＋模組偏移量（單位：秒，可以是負數，精度 0.0001 秒）。\n" +
                   "排序依校正後的時間：模組偏移量會改變不同模組之間的先後。原始 LOG 與原始時間不會改變，改回 0 即完全恢復；每次修改都會寫入稽核紀錄。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        void Row(string label, object source, string path, string id)
        {
            int r = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 8, 2) };
            var box = new TextBox { Margin = new Thickness(0, 2, 0, 2), HorizontalContentAlignment = HorizontalAlignment.Right };
            box.SetBinding(TextBox.TextProperty, new Binding(path) { Source = source, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            AutomationProperties.SetAutomationId(box, id);
            AutomationProperties.SetName(box, label);
            var unit = new TextBlock { Text = "秒", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            Grid.SetRow(text, r);
            Grid.SetRow(box, r);
            Grid.SetColumn(box, 1);
            Grid.SetRow(unit, r);
            Grid.SetColumn(unit, 2);
            grid.Children.Add(text);
            grid.Children.Add(box);
            grid.Children.Add(unit);
        }
        Row("整體（所有模組）", editor, nameof(TimeCorrectionViewModel.CaseOffset), "CaseOffset");
        foreach (var m in editor.Modules) Row($"模組 {m.Module}（額外）", m, nameof(ModuleOffsetViewModel.Offset), "ModuleOffset_" + m.Module);
        panel.Children.Add(grid);

        var error = new TextBlock { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        error.SetBinding(TextBlock.TextProperty, new Binding(nameof(TimeCorrectionViewModel.Error)));
        AutomationProperties.SetAutomationId(error, "TimeCorrectionError");
        panel.Children.Add(error);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var reset = new Button { Content = "全部改回 0", Padding = new Thickness(12, 3, 12, 3), Command = editor.ResetCommand };
        AutomationProperties.SetAutomationId(reset, "ResetTimeCorrection");
        var ok = new Button { Content = "確定", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(24, 0, 0, 0), IsDefault = true };
        ok.Click += (_, _) =>
        {
            if (editor.TryAccept()) DialogResult = true;
        };
        AutomationProperties.SetAutomationId(ok, "ApplyTimeCorrection");
        var cancel = new Button { Content = "取消", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        AutomationProperties.SetAutomationId(cancel, "CancelTimeCorrection");
        buttons.Children.Add(reset);
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;
    }
}
