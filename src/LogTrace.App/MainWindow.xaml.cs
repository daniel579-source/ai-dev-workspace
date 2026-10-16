using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using LogTrace.Workspace;

namespace LogTrace.App;

public partial class MainWindow : Window
{
    /// <summary>旗標欄：圖示＋滑鼠說明（ADR-0008 第 5 點）。</summary>
    static readonly DataTemplate FlagCell = (DataTemplate)XamlReader.Parse("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
            <TextBlock Text="{Binding FlagGlyphs}" ToolTip="{Binding FlagToolTip}" Foreground="#B35C00" FontWeight="Bold" />
        </DataTemplate>
        """);

    MainViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as MainViewModel);
    }

    void Attach(MainViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm.Modules.CollectionChanged -= OnModulesChanged;
        }
        _vm = vm;
        if (vm is null) return;
        vm.PropertyChanged += OnViewModelChanged;
        vm.Modules.CollectionChanged += OnModulesChanged;
        UpdateEventView();
    }

    void OnModulesChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEventView();

    void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsTrackView):
                UpdateEventView();
                break;
            case nameof(MainViewModel.RawSelectedLine) when _vm?.RawSelectedLine is { } line:
                // 跳到原始 LOG 中對應的行
                Dispatcher.BeginInvoke(() => RawLog.ScrollIntoView(line));
                break;
        }
    }

    /// <summary>混合檢視：一欄訊息、以底色區分模組；分軌檢視：每個模組一欄（ADR-0008 第 5 點）。</summary>
    void UpdateEventView()
    {
        if (_vm is null) return;
        var view = new GridView();
        view.Columns.Add(Text("序號", nameof(EventRowViewModel.Sequence), 70));
        view.Columns.Add(Text("時間", nameof(EventRowViewModel.Time), 180));
        view.Columns.Add(new GridViewColumn { Header = "旗標", CellTemplate = FlagCell, Width = 60 });
        if (_vm.IsTrackView)
        {
            foreach (var module in _vm.Modules)
                view.Columns.Add(Text(module, $"[{module}]", 360));
        }
        else
        {
            view.Columns.Add(Text("模組", nameof(EventRowViewModel.Module), 60));
            view.Columns.Add(Text("Logger", nameof(EventRowViewModel.Logger), 120));
            view.Columns.Add(Text("等級", nameof(EventRowViewModel.Level), 60));
            view.Columns.Add(Text("訊息", nameof(EventRowViewModel.Message), 520));
            view.Columns.Add(Text("來源", nameof(EventRowViewModel.Source), 320));
        }
        EventList.View = view;
        if (EventList.SelectedItem is { } selected) EventList.ScrollIntoView(selected);
    }

    /// <summary>回到主畫面時（例如關閉 Layout 編輯器後）重新列出廠別設定。</summary>
    void Window_Activated(object? sender, EventArgs e) => _vm?.RefreshPlants();

    void CaseList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_vm?.OpenSelectedCaseCommand.CanExecute(null) == true) _vm.OpenSelectedCaseCommand.Execute(null);
    }

    static GridViewColumn Text(string header, string path, double width) =>
        new() { Header = header, DisplayMemberBinding = new Binding(path), Width = width };
}

/// <summary>布林值反轉（混合檢視的選項按鈕）。</summary>
public sealed class NotConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is false;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is false;
}
