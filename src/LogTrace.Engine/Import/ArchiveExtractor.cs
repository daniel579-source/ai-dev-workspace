using System.IO.Compression;
using SevenZip;

namespace LogTrace.Engine.Import;

/// <summary>
/// 解開 7z、zip 壓縮檔（ADR-0002 第 2 點）。
/// <list type="bullet">
/// <item>7z：7-Zip 的 7za.dll（只支援 7z 格式、不含 unRAR，LGPL）加上 SevenZipSharp；7za.dll 隨程式放在 7z\ 子資料夾。</item>
/// <item>zip：.NET 內建的 System.IO.Compression。</item>
/// </list>
/// 任何項目的路徑跑出目的資料夾時，整個壓縮檔都不解開。
/// </summary>
public static class ArchiveExtractor
{
    public static readonly IReadOnlyList<string> Extensions = [".7z", ".zip"];

    static readonly Lock InitLock = new();
    static bool _initialized;

    public static bool IsArchive(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>7za.dll 的位置；只提供 64 位元版本。</summary>
    public static string LibraryPath => Path.Combine(AppContext.BaseDirectory, "7z", "7za.dll");

    /// <summary>解開到 destination（必須是空的資料夾）。</summary>
    public static void Extract(string archivePath, string destination, CancellationToken ct)
    {
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        if (string.Equals(Path.GetExtension(archivePath), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(archivePath);
            foreach (var entry in zip.Entries) CheckInside(root, entry.FullName);
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
                if (entry.Name.Length == 0) // 資料夾項目
                {
                    Directory.CreateDirectory(target);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: false);
            }
            return;
        }

        EnsureLibrary();
        using var extractor = new SevenZipExtractor(archivePath);
        foreach (var entry in extractor.ArchiveFileData) CheckInside(root, entry.FileName);
        ct.ThrowIfCancellationRequested();
        extractor.ExtractArchive(destination);
    }

    static void CheckInside(string root, string entryName)
    {
        var target = Path.GetFullPath(Path.Combine(root, entryName));
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"壓縮檔中有不安全的路徑：{entryName}");
    }

    static void EnsureLibrary()
    {
        lock (InitLock)
        {
            if (_initialized) return;
            if (!Environment.Is64BitProcess)
                throw new PlatformNotSupportedException("解開壓縮檔需要 64 位元的程式。");
            if (!File.Exists(LibraryPath))
                throw new FileNotFoundException("找不到解開壓縮檔所需的 7za.dll", LibraryPath);
            SevenZipBase.SetLibraryPath(LibraryPath);
            _initialized = true;
        }
    }
}
