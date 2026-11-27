using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LogTrace.Launcher;

/// <summary>
/// 解開升級包時的進度視窗（2026-11-27 使用者選 4b）：一行說明文字加一條進度列，不能關閉。
/// 主執行緒跑 Win32 訊息迴圈，工作在背景執行緒執行，完成後關閉視窗。
/// </summary>
static unsafe partial class ProgressWindow
{
    const uint WS_OVERLAPPED = 0x0, WS_CAPTION = 0xC00000, WS_SYSMENU = 0x80000, WS_VISIBLE = 0x10000000, WS_CHILD = 0x40000000;
    const uint WS_EX_TOPMOST = 0x8, WS_EX_DLGMODALFRAME = 0x1;
    const uint WM_DESTROY = 0x2, WM_CLOSE = 0x10, WM_SETFONT = 0x30, WM_APP_DONE = 0x8001;
    const uint PBM_SETRANGE32 = 0x406, PBM_SETPOS = 0x402;
    const int SM_CXSCREEN = 0, SM_CYSCREEN = 1, DEFAULT_GUI_FONT = 17, COLOR_WINDOW = 5;
    const uint ICC_PROGRESS_CLASS = 0x20;
    const int Scale = 1000;

    static bool _allowClose;

    [StructLayout(LayoutKind.Sequential)]
    struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public delegate* unmanaged<nint, uint, nint, nint, nint> lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground, lpszMenuName, lpszClassName, hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam, lParam;
        public uint time;
        public int x, y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct INITCOMMONCONTROLSEX
    {
        public uint dwSize, dwICC;
    }

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW")]
    private static partial ushort RegisterClassEx(WNDCLASSEXW* wc);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int w, int h,
        nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static partial nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    private static partial int GetMessage(MSG* msg, nint hWnd, uint min, uint max);

    [LibraryImport("user32.dll")]
    private static partial int TranslateMessage(MSG* msg);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static partial nint DispatchMessage(MSG* msg);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowText(nint hWnd, string text);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    private static partial void PostQuitMessage(int exitCode);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("gdi32.dll")]
    private static partial nint GetStockObject(int index);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW")]
    private static partial nint GetModuleHandle(nint name);

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitCommonControlsEx(INITCOMMONCONTROLSEX* icc);

    [UnmanagedCallersOnly]
    static nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_CLOSE when !_allowClose:
                return 0; // 解開中不能關閉
            case WM_APP_DONE:
                _allowClose = true;
                DestroyWindow(hWnd);
                return 0;
            case WM_DESTROY:
                PostQuitMessage(0);
                return 0;
            default:
                return DefWindowProc(hWnd, msg, wParam, lParam);
        }
    }

    /// <summary>顯示進度視窗並在背景執行 work，work 結束後關閉視窗才傳回。work 的例外會在這裡重新丟出。</summary>
    public static void Run(string title, Action<IProgress<string>, IProgress<double>> work)
    {
        var icc = new INITCOMMONCONTROLSEX { dwSize = (uint)sizeof(INITCOMMONCONTROLSEX), dwICC = ICC_PROGRESS_CLASS };
        InitCommonControlsEx(&icc);
        var instance = GetModuleHandle(0);
        const string className = "LogTraceLauncherProgress";
        var classNamePtr = Marshal.StringToHGlobalUni(className);
        try
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = instance,
                hbrBackground = COLOR_WINDOW + 1,
                lpszClassName = classNamePtr,
            };
            RegisterClassEx(&wc);
            const int width = 460, height = 140;
            var x = (GetSystemMetrics(SM_CXSCREEN) - width) / 2;
            var y = (GetSystemMetrics(SM_CYSCREEN) - height) / 2;
            var hwnd = CreateWindowEx(WS_EX_TOPMOST | WS_EX_DLGMODALFRAME, className, title, WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_VISIBLE,
                x, y, width, height, 0, 0, instance, 0);
            if (hwnd == 0)
            {
                // 無法建立視窗時仍然執行，只是沒有進度
                work(new Reporter<string>(_ => { }), new Reporter<double>(_ => { }));
                return;
            }
            var font = GetStockObject(DEFAULT_GUI_FONT);
            var label = CreateWindowEx(0, "STATIC", "準備中…", WS_CHILD | WS_VISIBLE, 16, 14, width - 48, 22, hwnd, 0, instance, 0);
            var bar = CreateWindowEx(0, "msctls_progress32", "", WS_CHILD | WS_VISIBLE, 16, 44, width - 48, 22, hwnd, 0, instance, 0);
            SendMessage(label, WM_SETFONT, font, 1);
            SendMessage(bar, PBM_SETRANGE32, 0, Scale);

            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    work(new Reporter<string>(s => SetWindowText(label, s)),
                        new Reporter<double>(f => SendMessage(bar, PBM_SETPOS, (nint)(Math.Clamp(f, 0, 1) * Scale), 0)));
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    PostMessage(hwnd, WM_APP_DONE, 0, 0);
                }
            }) { IsBackground = true };
            thread.Start();

            MSG msg;
            while (GetMessage(&msg, 0, 0, 0) > 0)
            {
                TranslateMessage(&msg);
                DispatchMessage(&msg);
            }
            thread.Join();
            if (failure is not null) throw new IOException(failure.Message, failure);
        }
        finally
        {
            Marshal.FreeHGlobal(classNamePtr);
        }
    }

    sealed class Reporter<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
