using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace LogTrace.App;

/// <summary>
/// 畫面執行緒被佔住時（例如把十幾萬筆結果放進事件清單、重新繪製時間軸），主視窗無法更新，看起來像當機。
/// 這裡用另一條執行緒監看：畫面執行緒超過 1 秒沒有回應時，在主視窗中央另開「處理中」視窗；
/// 這個視窗有自己的畫面執行緒，主視窗被佔住時仍然會動。畫面執行緒恢復回應後自動關閉。
/// 背景工作期間（畫面執行緒仍有回應）由主視窗狀態列的忙碌表示負責。
/// </summary>
sealed class BusyWatchdog : IDisposable
{
    static readonly TimeSpan Threshold = TimeSpan.FromSeconds(1);
    const int IntervalMs = 250;

    readonly Dispatcher _main;
    readonly IntPtr _mainHandle;
    readonly Dispatcher _busy;
    readonly Thread _monitor;
    readonly ManualResetEvent _stop = new(false);
    BusyWindow? _window; // 只在 _busy 的執行緒上使用
    long _lastAnswer = Environment.TickCount64;
    int _pending;
    volatile string? _activity;

    public BusyWatchdog(Window main)
    {
        _main = main.Dispatcher;
        _mainHandle = new WindowInteropHelper(main).Handle;

        Dispatcher? busy = null;
        using var ready = new ManualResetEventSlim();
        var ui = new Thread(() =>
        {
            busy = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true, Name = "LogTrace 處理中視窗" };
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        ready.Wait();
        _busy = busy!;

        _monitor = new Thread(Monitor) { IsBackground = true, Name = "LogTrace 畫面監看" };
        _monitor.Start();
    }

    /// <summary>目前的工作（例如「匯入中…」）；null 表示沒有進行中的工作，只是在更新畫面。由主視窗設定。</summary>
    public string? Activity
    {
        set => _activity = value;
    }

    void Monitor()
    {
        bool showing = false;
        int answered = 0; // 顯示中連續有回應的次數
        int calm = (int)(Threshold.TotalMilliseconds / IntervalMs);
        while (!_stop.WaitOne(IntervalMs))
        {
            if (_main.HasShutdownStarted) return;
            // 先看上一個小工作是否已被處理，再送下一個（順序相反時永遠看到「等待中」，視窗不會關閉）
            bool waiting = Volatile.Read(ref _pending) == 1;
            long last = Volatile.Read(ref _lastAnswer);
            if (!waiting)
            {
                // 送一個小工作給畫面執行緒；它處理到時就表示有回應（與使用者的輸入同一個優先順序）
                Volatile.Write(ref _pending, 1);
                _main.BeginInvoke(DispatcherPriority.Input, () =>
                {
                    Volatile.Write(ref _lastAnswer, Environment.TickCount64);
                    Volatile.Write(ref _pending, 0);
                });
            }

            if (!showing)
            {
                if (waiting && Environment.TickCount64 - last >= Threshold.TotalMilliseconds)
                {
                    showing = true;
                    answered = 0;
                    var activity = _activity;
                    _busy.BeginInvoke(() => ShowWindow(last, activity));
                }
            }
            else
            {
                // 大量資料的更新常是幾段長時間的工作，中間短暫有回應；連續 1 秒都有回應才關閉，避免視窗一閃一閃
                answered = waiting ? 0 : answered + 1;
                if (answered >= calm)
                {
                    showing = false;
                    _busy.BeginInvoke(() => _window?.End());
                }
            }
        }
    }

    void ShowWindow(long since, string? activity)
    {
        // 主視窗縮到最小或看不見時不顯示
        if (!NativeMethods.IsWindowVisible(_mainHandle) || NativeMethods.IsIconic(_mainHandle)) return;
        _window ??= new BusyWindow();
        _window.Begin(since, activity);
        // 置中於主視窗（實際像素；跨執行緒讀取視窗位置不會等待主視窗）
        if (NativeMethods.GetWindowRect(_mainHandle, out var main) &&
            NativeMethods.GetWindowRect(new WindowInteropHelper(_window).Handle, out var own))
        {
            int x = main.Left + (main.Right - main.Left - (own.Right - own.Left)) / 2;
            int y = main.Top + (main.Bottom - main.Top - (own.Bottom - own.Top)) / 2;
            NativeMethods.SetWindowPos(new WindowInteropHelper(_window).Handle, IntPtr.Zero, x, y, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }
    }

    public void Dispose()
    {
        _stop.Set();
        _monitor.Join();
        _busy.InvokeShutdown();
        _stop.Dispose();
    }

    static class NativeMethods
    {
        public const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr hWnd);
    }
}

/// <summary>「處理中」視窗：會動的進度條與經過秒數，說明程式沒有當機。不搶焦點、不能由使用者關閉。</summary>
sealed class BusyWindow : Window
{
    readonly TextBlock _detail = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    readonly TextBlock _elapsed = new() { Foreground = Brushes.DimGray, Margin = new Thickness(0, 6, 0, 0) };
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };
    long _since;

    public BusyWindow()
    {
        Title = "LogTrace 處理中";
        AutomationProperties.SetAutomationId(this, "BusyWindow");
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = Top = -32000; // 顯示後再置中於主視窗
        var panel = new StackPanel { Width = 380 };
        panel.Children.Add(new TextBlock { Text = "LogTrace 正在處理中，請稍候…", FontSize = 15, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 14, Margin = new Thickness(0, 10, 0, 0) });
        panel.Children.Add(_detail);
        panel.Children.Add(new TextBlock
        {
            Text = "程式沒有當機。資料量大時需要一些時間，完成後這個視窗會自動關閉。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
        });
        panel.Children.Add(_elapsed);
        Content = new Border
        {
            Background = Brushes.White, BorderBrush = Brushes.SteelBlue, BorderThickness = new Thickness(2),
            Padding = new Thickness(18, 14, 18, 14), Child = panel,
        };
        _tick.Tick += (_, _) => UpdateElapsed();
        Closing += (_, e) => e.Cancel = true; // 由監看器控制顯示與隱藏
    }

    public void Begin(long since, string? activity)
    {
        _since = since;
        _detail.Text = activity is null ? "正在更新畫面（事件清單、時間軸等）。" : $"目前的工作：{activity}";
        UpdateElapsed();
        _tick.Start();
        Show();
    }

    public void End()
    {
        _tick.Stop();
        Hide();
    }

    void UpdateElapsed() => _elapsed.Text = $"已經過 {(Environment.TickCount64 - _since) / 1000} 秒";
}
