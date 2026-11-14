using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using LogTrace.Workspace.Playback;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace LogTrace.App;

/// <summary>
/// Layout 回放的 3D 畫面（ADR-0017）：WebView2 內嵌 three.js。
/// <list type="bullet">
/// <item>只使用程式資料夾內的固定版本 Runtime（WebView2Runtime\），不改用電腦上安裝的版本。</item>
/// <item>只載入程式資料夾內的網頁（playback3d\，以虛擬主機名稱對應）；其他所有網路請求與外部導覽一律封鎖。</item>
/// <item>C# 傳場景資料與目前時刻的狀態給網頁；網頁回報載入完成、錯誤與點選的元素。</item>
/// <item>WebView2 或 WebGL 不能使用時顯示原因，其他面板照常（第 9 點）。</item>
/// </list>
/// </summary>
public sealed class Playback3DView : Grid
{
    /// <summary>網頁的虛擬主機名稱（.invalid 保留給不存在的網域，不會被解析到網路上）。</summary>
    const string Host = "logtrace.invalid";
    const string StartPage = $"https://{Host}/index.html";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly TextBlock _status = new() { Foreground = Brushes.DimGray, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09)), Margin = new Thickness(12) };
    readonly Border _errorPanel;
    WebView2? _web;
    PlaybackViewModel? _vm;
    bool _ready;
    int _blocked;
    int _elements;

    /// <summary>WebView2 的使用者資料資料夾（快取等）；由主視窗設定。</summary>
    public string? UserDataFolder { get; set; }

    public Playback3DView()
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 3, 6, 3) };
        var reset = new Button { Content = "回到預設視角", Padding = new Thickness(8, 1, 8, 1) };
        AutomationProperties.SetAutomationId(reset, "Playback3DResetView");
        reset.Click += (_, _) => Post("resetView", null);
        var follow = new CheckBox { Content = "跟隨（選取時轉向元素）", Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "從設備狀態表、命令或事件選取元素時，3D 相機轉向該元素" };
        follow.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new System.Windows.Data.Binding(nameof(PlaybackViewModel.FollowFocus)));
        AutomationProperties.SetAutomationId(follow, "PlaybackFollowFocus");
        AutomationProperties.SetAutomationId(_status, "Playback3DStatus");
        bar.Children.Add(reset);
        bar.Children.Add(follow);
        bar.Children.Add(_status);
        var top = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)), BorderThickness = new Thickness(0, 0, 0, 1), Child = bar };
        Children.Add(top);

        AutomationProperties.SetAutomationId(_error, "Playback3DError");
        _errorPanel = new Border { Background = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC)), Child = _error, Visibility = Visibility.Collapsed };
        SetRow(_errorPanel, 1);
        Children.Add(_errorPanel);

        DataContextChanged += (_, _) => Attach(DataContext as PlaybackViewModel);
        Loaded += async (_, _) => await StartAsync();
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
        SendScene();
    }

    void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaybackViewModel.Scene)) SendScene();
        else if (e.PropertyName == nameof(PlaybackViewModel.SceneNow)) Post("state", _vm!.SceneNow);
    }

    void OnFocusRequested(string id) => Post("focus", id);

    void SendScene()
    {
        if (_vm is null) return;
        Post("scene", _vm.Scene);
        Post("state", _vm.SceneNow);
    }

    void Post(string kind, object? data)
    {
        if (!_ready || _web?.CoreWebView2 is not { } core) return;
        try
        {
            core.PostWebMessageAsJson(PlaybackScene.Message(kind, data!));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            ShowError($"無法更新 3D 畫面：{ex.Message}");
        }
    }

    /// <summary>程式資料夾內的固定版本 Runtime。</summary>
    static string RuntimeFolder => Path.Combine(AppContext.BaseDirectory, "WebView2Runtime");

    static string PageFolder => Path.Combine(AppContext.BaseDirectory, "playback3d");

    async Task StartAsync()
    {
        if (_web is not null) return;
        if (!File.Exists(Path.Combine(RuntimeFolder, "msedgewebview2.exe")))
        {
            ShowError($"找不到程式資料夾內的 WebView2 Runtime（{RuntimeFolder}），無法顯示 3D 回放。其他功能（事件清單、詳細資訊、設備狀態表等）照常使用。");
            return;
        }
        if (!File.Exists(Path.Combine(PageFolder, "index.html")))
        {
            ShowError($"找不到 3D 回放的網頁（{PageFolder}）。其他功能照常使用。");
            return;
        }
        try
        {
            // 封鎖所有網路：DNS 一律失敗，並關閉背景連線、元件更新與同步（ADR-0001、ADR-0017 第 3 點）
            var options = new CoreWebView2EnvironmentOptions(
                "--host-resolver-rules=\"MAP * ~NOTFOUND\" --disable-background-networking --disable-component-update --disable-sync " +
                "--disable-domain-reliability --no-pings --disable-features=msSmartScreenProtection,AutofillServerCommunication")
            { AllowSingleSignOnUsingOSPrimaryAccount = false };
            var env = await CoreWebView2Environment.CreateAsync(RuntimeFolder, UserDataFolder, options);
            _web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0xF8, 0xFA, 0xFC) };
            AutomationProperties.SetAutomationId(_web, "Playback3D");
            SetRow(_web, 1);
            Children.Insert(1, _web);
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;
            var s = core.Settings;
            s.AreDefaultContextMenusEnabled = false;
            s.AreDevToolsEnabled = false;
            s.IsStatusBarEnabled = false;
            s.IsZoomControlEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = false;
            s.IsPasswordAutosaveEnabled = false;
            s.IsGeneralAutofillEnabled = false;
            s.IsReputationCheckingRequired = false;
            s.AreHostObjectsAllowed = false;
            core.SetVirtualHostNameToFolderMapping(Host, PageFolder, CoreWebView2HostResourceAccessKind.Deny);
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                if (Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == Host) return;
                _blocked++;
                e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
                UpdateStatus();
            };
            core.NavigationStarting += (_, e) =>
            {
                if (!e.Uri.StartsWith($"https://{Host}/", StringComparison.Ordinal)) e.Cancel = true;
            };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.ProcessFailed += (_, e) => ShowError($"3D 畫面的瀏覽器程序停止（{e.ProcessFailedKind}）；請重新開啟案件。其他功能照常使用。");
            core.WebMessageReceived += OnWebMessage;
            core.Navigate(StartPage);
        }
        catch (Exception ex)
        {
            ShowError($"WebView2 無法啟動，無法顯示 3D 回放：{ex.Message}。其他功能照常使用。");
        }
    }

    void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        PageMessage? msg;
        try
        {
            msg = JsonSerializer.Deserialize<PageMessage>(e.WebMessageAsJson, Json);
        }
        catch (JsonException)
        {
            return;
        }
        switch (msg?.Kind)
        {
            case "ready":
                _ready = true;
                SendScene();
                break;
            case "loaded":
                _elements = msg.Elements ?? 0;
                UpdateStatus();
                break;
            case "error":
                ShowError((msg.Message ?? "3D 畫面發生錯誤") + "。其他功能照常使用。");
                break;
            case "pick":
                _vm?.SelectFromScene(msg.Id, msg.Column);
                break;
        }
    }

    /// <summary>關閉程式時結束瀏覽器程序（放開使用者資料資料夾）。</summary>
    public void Shutdown()
    {
        _ready = false;
        _web?.Dispose();
        _web = null;
    }

    void UpdateStatus() =>
        _status.Text = $"3D 已載入：{_elements} 個元素" + (_blocked > 0 ? $"；已封鎖 {_blocked} 個網路請求" : "");

    void ShowError(string message)
    {
        _error.Text = message;
        _errorPanel.Visibility = Visibility.Visible;
        if (_web is not null) _web.Visibility = Visibility.Collapsed;
    }

    sealed record PageMessage(string? Kind, string? Message, int? Elements, string? Id, int? Column);
}
