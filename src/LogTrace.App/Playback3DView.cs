using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using LogTrace.Workspace.Playback;

namespace LogTrace.App;

/// <summary>
/// Layout 回放的 3D 畫面（ADR-0017）：C# 傳場景資料與目前時刻的狀態給網頁；網頁回報載入完成、錯誤與點選的元素。
/// </summary>
public sealed class Playback3DView : Web3DView
{
    PlaybackViewModel? _vm;

    protected override string Page => "index.html";
    protected override string What => " 3D 回放";
    protected override string AutomationPrefix => "Playback3D";
    protected override string OtherFeatures => "其他功能（事件清單、詳細資訊、設備狀態表等）照常使用。";

    public Playback3DView()
    {
        ToolBar.Children.Add(ToolButton("回到預設視角", "Playback3DResetView", () => Post("resetView", null)));
        var follow = new CheckBox { Content = "跟隨（選取時轉向元素）", Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "從設備狀態表、命令或事件選取元素時，3D 相機轉向該元素" };
        follow.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new System.Windows.Data.Binding(nameof(PlaybackViewModel.FollowFocus)));
        AutomationProperties.SetAutomationId(follow, "PlaybackFollowFocus");
        ToolBar.Children.Add(follow);
        // 搬送中貨物的標示（#125）：命令編號（預設）或棧板編號，記在本機
        ToolBar.Children.Add(new TextBlock { Text = "貨物標示", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 4, 0) });
        var cargo = new ComboBox { ItemsSource = PlaybackViewModel.CargoLabelChoices, Width = 90, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "搬送中的貨物（在搬送機、RM、電梯上）顯示命令編號或棧板編號" };
        cargo.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedIndexProperty, new System.Windows.Data.Binding(nameof(PlaybackViewModel.CargoLabelIndex)));
        AutomationProperties.SetAutomationId(cargo, "PlaybackCargoLabel");
        ToolBar.Children.Add(cargo);
        DataContextChanged += (_, _) => Attach(DataContext as PlaybackViewModel);
    }

    void Attach(PlaybackViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm.FocusRequested -= OnFocusRequested;
        }
        _vm = vm;
        if (vm is null) return;
        vm.PropertyChanged += OnViewModelChanged;
        vm.FocusRequested += OnFocusRequested;
        OnReady();
    }

    void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaybackViewModel.Scene)) OnReady();
        else if (e.PropertyName == nameof(PlaybackViewModel.SceneNow)) Post("state", _vm!.SceneNow);
    }

    void OnFocusRequested(string id) => Post("focus", id);

    protected override void OnReady()
    {
        if (_vm is null) return;
        Post("scene", _vm.Scene);
        Post("state", _vm.SceneNow);
    }

    protected override void OnPageMessage(string kind, JsonElement message)
    {
        if (kind != "pick") return;
        var id = message.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
        int? column = message.TryGetProperty("column", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : null;
        _vm?.SelectFromScene(id, column);
    }
}
