using System.Runtime.InteropServices;

namespace LogTrace.Launcher;

/// <summary>Win32 MessageBox（2026-11-27 使用者選 1a：確認畫面用 MessageBox）。</summary>
static partial class Win32
{
    const uint MB_OK = 0x0, MB_YESNO = 0x4, MB_ICONERROR = 0x10, MB_ICONQUESTION = 0x20, MB_ICONINFORMATION = 0x40, MB_SETFOREGROUND = 0x10000;
    const int IDYES = 6;

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(nint hWnd, string text, string caption, uint type);

    public static bool AskYesNo(string title, string text) => MessageBox(0, text, title, MB_YESNO | MB_ICONQUESTION | MB_SETFOREGROUND) == IDYES;

    public static void Info(string title, string text) => MessageBox(0, text, title, MB_OK | MB_ICONINFORMATION | MB_SETFOREGROUND);

    public static void Error(string title, string text) => MessageBox(0, text, title, MB_OK | MB_ICONERROR | MB_SETFOREGROUND);
}
