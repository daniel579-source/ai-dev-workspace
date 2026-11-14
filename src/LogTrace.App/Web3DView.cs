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
/// 3D 畫面的共用部分（ADR-0017、ADR-0018）：WebView2 內嵌 three.js。Layout 回放與 Layout 編輯器各自繼承。
/// <list type="bullet">
/// <item>只使用程式資料夾內的固定版本 Runtime（WebView2Runtime\），不改用電腦上安裝的版本。</item>
/// <item>只載入程式資料夾內的網頁（web3d\，以虛擬主機名稱對應）；其他所有網路請求與外部導覽一律封鎖。</item>
/// <item>WebView2 或 WebGL 不能使用時顯示原因，其他功能照常。</item>
/// </list>
/// 上方工具列由子類別加入按鈕；狀態文字（Web3DStatus）顯示載入的元素數與被封鎖的網路請求數。
/// </summary>
public abstract class Web3DView : Grid
{
    /// <summary>網頁的虛擬主機名稱（.invalid 保留給不存在的網域，不會被解析到網路上）。</summary>
    const string Host = "logtrace.invalid";

    /// <summary>WebView2 的使用者資料資料夾；程式啟動時設定，回放與編輯器共用同一個瀏覽器環境。</summary>
    public static string? DefaultUserDataFolder { get; set; }

    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly TextBlock _status = new() { Foreground = Brushes.DimGray, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09)), Margin = new Thickness(12) };
    readonly Border _errorPanel;
    WebView2? _web;
    bool _ready;
    int _blocked;
    int _elements;

    /// <summary>上方工具列（子類別加入按鈕）。</summary>
    protected StackPanel ToolBar { get; } = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 3, 6, 3) };

    /// <summary>網頁的檔名（web3d\ 下），例如 index.html。</summary>
    protected abstract string Page { get; }

    /// <summary>不能使用時的說明中，這個畫面的名稱（例如「3D 回放」）。</summary>
    protected abstract string What { get; }

    /// <summary>UI 自動化的識別碼前綴（例如 Playback3D）：WebView 為前綴本身，狀態與錯誤加上 Status、Error。</summary>
    protected abstract string AutomationPrefix { get; }

    protected Web3DView()
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        var top = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)), BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new DockPanel { Children = { _status, ToolBar } },
        };
        DockPanel.SetDock(_status, Dock.Right);
        Children.Add(top);
        _errorPanel = new Border { Background = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC)), Child = _error, Visibility = Visibility.Collapsed };
        SetRow(_errorPanel, 1);
        Children.Add(_errorPanel);
        Loaded += async (_, _) =>
        {
            AutomationProperties.SetAutomationId(_status, AutomationPrefix + "Status");
            AutomationProperties.SetAutomationId(_error, AutomationPrefix + "Error");
            await StartAsync();
        };
    }

    protected static Button ToolButton(string text, string automationId, Action click, string? toolTip = null)
    {
        var b = new Button { Content = text, Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 6, 0), ToolTip = toolTip };
        AutomationProperties.SetAutomationId(b, automationId);
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>網頁已準備好接收訊息。</summary>
    protected bool IsReady => _ready;

    /// <summary>傳訊息給網頁：{ "kind": …, "data": … }。網頁還沒準備好時不傳（準備好時會呼叫 <see cref="OnReady"/>）。</summary>
    protected void Post(string kind, object? data)
    {
        if (!_ready || _web?.CoreWebView2 is not { } core) return;
        try
        {
            core.PostWebMessageAsJson(PlaybackScene.Message(kind, data!));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            ShowError($"無法更新{What}：{ex.Message}");
        }
    }

    /// <summary>網頁載入完成、可以接收訊息：子類別傳送場景。</summary>
    protected abstract void OnReady();

    /// <summary>網頁傳來的其他訊息（ready、loaded、error 以外）。</summary>
    protected abstract void OnPageMessage(string kind, JsonElement message);

    static string RuntimeFolder => Path.Combine(AppContext.BaseDirectory, "WebView2Runtime");

    static string PageFolder => Path.Combine(AppContext.BaseDirectory, "web3d");

    async Task StartAsync()
    {
        if (_web is not null) return;
        if (!File.Exists(Path.Combine(RuntimeFolder, "msedgewebview2.exe")))
        {
            ShowError($"找不到程式資料夾內的 WebView2 Runtime（{RuntimeFolder}），無法顯示{What}。{OtherFeatures}");
            return;
        }
        if (!File.Exists(Path.Combine(PageFolder, Page)))
        {
            ShowError($"找不到{What}的網頁（{Path.Combine(PageFolder, Page)}）。{OtherFeatures}");
            return;
        }
        try
        {
            // 封鎖所有網路：DNS 一律失敗，並關閉背景連線、元件更新與同步（ADR-0001、ADR-0017 第 3 點）
            var options = new CoreWebView2EnvironmentOptions(
                "--host-resolver-rules=\"MAP * ~NOTFOUND\" --disable-background-networking --disable-component-update --disable-sync " +
                "--disable-domain-reliability --no-pings --disable-features=msSmartScreenProtection,AutofillServerCommunication")
            { AllowSingleSignOnUsingOSPrimaryAccount = false };
            var env = await CoreWebView2Environment.CreateAsync(RuntimeFolder, DefaultUserDataFolder, options);
            _web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0xF8, 0xFA, 0xFC) };
            AutomationProperties.SetAutomationId(_web, AutomationPrefix);
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
            core.ProcessFailed += (_, e) => ShowError($"{What}的瀏覽器程序停止（{e.ProcessFailedKind}）；請關閉後重新開啟。{OtherFeatures}");
            core.WebMessageReceived += OnWebMessage;
            core.Navigate($"https://{Host}/{Page}");
        }
        catch (Exception ex)
        {
            ShowError($"WebView2 無法啟動，無法顯示{What}：{ex.Message}。{OtherFeatures}");
        }
    }

    /// <summary>不能使用時，告訴使用者其他功能照常（子類別可以改寫）。</summary>
    protected virtual string OtherFeatures => "其他功能照常使用。";

    void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonElement msg;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            msg = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }
        if (msg.ValueKind != JsonValueKind.Object || !msg.TryGetProperty("kind", out var k) || k.GetString() is not { } kind) return;
        switch (kind)
        {
            case "ready":
                _ready = true;
                OnReady();
                break;
            case "loaded":
                _elements = msg.TryGetProperty("elements", out var n) && n.TryGetInt32(out var count) ? count : 0;
                UpdateStatus();
                break;
            case "error":
                ShowError(((msg.TryGetProperty("message", out var m) ? m.GetString() : null) ?? "3D 畫面發生錯誤") + "。" + OtherFeatures);
                break;
            default:
                OnPageMessage(kind, msg);
                break;
        }
    }

    /// <summary>關閉視窗時結束瀏覽器程序（放開使用者資料資料夾）。</summary>
    public void Shutdown()
    {
        _ready = false;
        _web?.Dispose();
        _web = null;
    }

    void UpdateStatus() =>
        _status.Text = $"3D 已載入：{_elements} 個元素" + (_blocked > 0 ? $"；已封鎖 {_blocked} 個網路請求" : "");

    protected void ShowError(string message)
    {
        _error.Text = message;
        _errorPanel.Visibility = Visibility.Visible;
        if (_web is not null) _web.Visibility = Visibility.Collapsed;
    }
}
