using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
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

    /// <summary>訊息欄：搜尋符合處醒目標示（ADR-0010 第 4 點）。</summary>
    static readonly DataTemplate MessageCell = (DataTemplate)XamlReader.Parse("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                      xmlns:local="clr-namespace:LogTrace.App;assembly=LogTrace">
            <TextBlock local:Highlight.Segments="{Binding MessageSegments}" ToolTip="{Binding MatchNote}" />
        </DataTemplate>
        """);

    MainViewModel? _vm;
    DockLayouts? _layouts;

    /// <summary>使用者保存的版面檔（依使用者保存，不依案件，ADR-0008 第 7 點）；null 時不保存。</summary>
    public string? LayoutFile { get; init; }

    readonly System.Windows.Threading.DispatcherTimer _playTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    DateTime _lastTick;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as MainViewModel);
        OverviewTimeline.Seek = t => _vm?.SeekTime(t);
        DetailTimeline.Seek = t => _vm?.SeekTime(t);
        // 播放：以實際經過的時間推進目前時刻（ADR-0008 第 2 點）
        _playTimer.Tick += (_, _) =>
        {
            var now = DateTime.UtcNow;
            _vm?.Tick(now - _lastTick);
            _lastTick = now;
        };
    }

    /// <summary>
    /// 回放的按鍵（ADR-0008 第 1 點）：→／← 狀態變化、Shift 逐筆事件、Ctrl 符合篩選的事件、空白鍵播放、Home／End。
    /// 焦點在輸入框時不攔截，避免影響打字。
    /// </summary>
    void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_vm is null || Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or ComboBox or ComboBoxItem) return;
        var mods = Keyboard.Modifiers;
        System.Windows.Input.ICommand? command = (e.Key, mods) switch
        {
            (Key.Right, ModifierKeys.None) => _vm.NextChangeCommand,
            (Key.Left, ModifierKeys.None) => _vm.PreviousChangeCommand,
            (Key.Right, ModifierKeys.Shift) => _vm.NextEventCommand,
            (Key.Left, ModifierKeys.Shift) => _vm.PreviousEventCommand,
            (Key.Right, ModifierKeys.Control) => _vm.NextMatchCommand,
            (Key.Left, ModifierKeys.Control) => _vm.PreviousMatchCommand,
            (Key.Space, ModifierKeys.None) => _vm.TogglePlayCommand,
            (Key.Home, ModifierKeys.None) => _vm.GoToStartCommand,
            (Key.End, ModifierKeys.None) => _vm.GoToEndCommand,
            _ => null,
        };
        if (command is null) return;
        if (command.CanExecute(null)) command.Execute(null);
        e.Handled = true;
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
            case nameof(MainViewModel.IsPlaying):
                _lastTick = DateTime.UtcNow;
                if (_vm!.IsPlaying) _playTimer.Start();
                else _playTimer.Stop();
                break;
            case nameof(MainViewModel.SelectedEvent) when _vm?.SelectedEvent is { } selected:
                EventList.ScrollIntoView(selected); // 目前時刻移動時（方向鍵、播放、時間軸），事件清單跟著捲動
                break;
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
            view.Columns.Add(new GridViewColumn { Header = "訊息", CellTemplate = MessageCell, Width = 520 });
            view.Columns.Add(Text("來源", nameof(EventRowViewModel.Source), 320));
        }
        EventList.View = view;
        if (EventList.SelectedItem is { } selected) EventList.ScrollIntoView(selected);
    }

    /// <summary>Layout 回放：點元素選取它（料架的列另外處理）。</summary>
    void PlaybackElement_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: LogTrace.Workspace.Playback.PlaybackElementViewModel el } && !e.Handled)
            _vm?.Playback.SelectElementCommand.Execute(el);
    }

    // Layout 回放的縮放（ADR-0006 第 11 點）
    void PlaybackScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || _vm is null) return;
        _vm.Playback.ZoomStep(e.Delta > 0);
        e.Handled = true;
    }

    void PlaybackZoomFit_Click(object sender, RoutedEventArgs e) =>
        _vm?.Playback.FitZoom(PlaybackScroll.ViewportWidth - 16, PlaybackScroll.ViewportHeight - 16, MetersToPixelsConverter.PixelsPerMeter);

    void PlaybackZoomReset_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) _vm.Playback.Zoom = 1;
    }

    /// <summary>Layout 回放：點料架的一列顯示側視圖。</summary>
    void RackCell_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: LogTrace.Workspace.Playback.RackCellViewModel cell })
        {
            _vm?.Playback.SelectRackCell(cell);
            e.Handled = true;
        }
    }

    /// <summary>啟動時還原上次的版面；沒有保存過時使用「回放」預設版面。</summary>
    void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _layouts = new DockLayouts(Dock);
        string? saved = null;
        try
        {
            if (LayoutFile is not null && File.Exists(LayoutFile)) saved = File.ReadAllText(LayoutFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        if (saved is null) _layouts.ApplyPreset(DockLayouts.Playback);
        else if (!_layouts.TryRestore(saved)) _vm?.ShowStatus("保存的版面無法使用（檔案損壞或來自其他版本的程式），已改用預設版面。");
    }

    /// <summary>關閉程式時保存目前的版面；保存失敗不阻擋關閉。</summary>
    void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_layouts is null || LayoutFile is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LayoutFile)!);
            File.WriteAllText(LayoutFile, _layouts.Save());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    void PlaybackLayout_Click(object sender, RoutedEventArgs e) => _layouts?.ApplyPreset(DockLayouts.Playback);

    void SearchLayout_Click(object sender, RoutedEventArgs e) => _layouts?.ApplyPreset(DockLayouts.Search);

    /// <summary>還原預設：與初次啟動的版面（回放）相同。</summary>
    void ResetLayout_Click(object sender, RoutedEventArgs e) => _layouts?.ApplyPreset(DockLayouts.Playback);

    /// <summary>回到主畫面時（例如關閉 Layout 編輯器後）重新列出廠別設定。</summary>
    void Window_Activated(object? sender, EventArgs e) => _vm?.RefreshPlants();

    /// <summary>命令串起來的事件：點一筆跳到該時刻（ADR-0008 第 4 點）。</summary>
    void CommandEventList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CommandEventList.SelectedItem is EventRowViewModel row) _vm?.GoToCommandEventCommand.Execute(row);
    }

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

/// <summary>true 時隱藏、false 時顯示。</summary>
public sealed class CollapsedWhenTrueConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>數值大於 0 時為 true。</summary>
public sealed class PositiveConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is int n && n > 0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
