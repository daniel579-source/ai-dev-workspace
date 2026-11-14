namespace LogTrace.Tests.Support;

/// <summary>測試用的暫存資料夾；結束時刪除（原始檔複本為唯讀，刪除前先解除）。</summary>
public sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LogTraceTests", Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public static string Fixture(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    /// <summary>
    /// 刪除資料夾。程式關閉後，3D 回放的瀏覽器程序（WebView2）可能還要一下子才結束並放開使用者資料資料夾，
    /// 所以檔案被佔用時最多重試 10 秒。
    /// </summary>
    public void Dispose()
    {
        for (int attempt = 0; ; attempt++)
        {
            if (!Directory.Exists(Path)) return;
            try
            {
                foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(Path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 50)
            {
                Thread.Sleep(200);
            }
        }
    }
}
