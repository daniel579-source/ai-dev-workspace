namespace LogTrace.Tests.Support;

/// <summary>測試用的暫存資料夾；結束時刪除（原始檔複本為唯讀，刪除前先解除）。</summary>
public sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LogTraceTests", Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public static string Fixture(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    public void Dispose()
    {
        if (!Directory.Exists(Path)) return;
        foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(Path, recursive: true);
    }
}
