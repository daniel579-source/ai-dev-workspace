using System.IO.Compression;

namespace LogTrace.Update;

/// <summary>套用時的錯誤；訊息會寫進 rejected\ 的原因說明。</summary>
public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// 套用升級包（ADR-0030 第 3 點）：檢查磁碟空間 → 解開到 &lt;目的&gt;.partial（逐一核對 CRC）→ 寫入 installed.json → 改名成正式資料夾。
/// 成功時刪除升級包；失敗時刪除解開一半的資料夾，升級包移到 rejected\ 並寫原因。
/// </summary>
public static class UpdateInstaller
{
    /// <summary>解開後至少要再留下的空間。</summary>
    public const long SpaceMargin = 200L * 1024 * 1024;

    /// <summary>剩餘空間的來源（測試可以替換）。</summary>
    public static Func<string, long> FreeSpace { get; set; } = path => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;

    /// <summary>解開一個升級包。progress 回報 0～1。失敗時丟出 <see cref="UpdateException"/>，解開一半的資料夾已刪除。</summary>
    public static void Install(ProgramFolder folder, PlanItem item, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var m = item.Manifest;
        var target = m.IsApp ? folder.AppFolder(m.AppVersion) : folder.RuntimeFolder(m.Version);
        var needed = item.Package.UncompressedBytes + SpaceMargin;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var free = FreeSpace(target);
        if (free < needed)
            throw new UpdateException($"磁碟空間不足：解開 {m.Describe()} 需要約 {Mb(needed)}，目前只剩 {Mb(free)}。請清出空間後再把升級包放回 updates\\。");

        var partial = target + ".partial";
        try
        {
            if (Directory.Exists(partial)) Directory.Delete(partial, recursive: true);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true); // 不完整（沒有 installed.json）的舊資料夾
            Extract(item.Package.Path, m.PayloadPrefix, partial, item.Package.UncompressedBytes, progress, ct);
            File.WriteAllText(Path.Combine(partial, ProgramFolder.InstalledFileName), m.ToJson());
            Directory.Move(partial, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException)
        {
            TryDelete(partial);
            throw ex is OperationCanceledException ? new UpdateException("已取消", ex) : new UpdateException($"解開 {m.Describe()} 失敗：{ex.Message}", ex);
        }
        progress?.Report(1);
    }

    static void Extract(string zipPath, string prefix, string dest, long total, IProgress<double>? progress, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var root = Path.GetFullPath(dest) + Path.DirectorySeparatorChar;
        var buffer = new byte[1 << 20];
        long done = 0;
        foreach (var e in zip.Entries)
        {
            var name = e.FullName.Replace('\\', '/');
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rel = name[prefix.Length..];
            if (rel.Length == 0) continue;
            var path = Path.GetFullPath(Path.Combine(dest, rel));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"升級包中有不安全的路徑：{e.FullName}");
            if (name.EndsWith('/'))
            {
                Directory.CreateDirectory(path);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            uint crc = Crc32.Initial;
            using (var input = e.Open())
            using (var output = File.Create(path))
            {
                int n;
                while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, n);
                    crc = Crc32.Update(crc, buffer.AsSpan(0, n));
                    done += n;
                    if (total > 0) progress?.Report(Math.Min(0.99, (double)done / total));
                }
            }
            if (Crc32.Finish(crc) != e.Crc32) throw new InvalidDataException($"檔案內容與壓縮檔記錄的 CRC 不符（升級包損壞）：{rel}");
            File.SetLastWriteTime(path, e.LastWriteTime.LocalDateTime);
        }
    }

    /// <summary>套用成功：刪除升級包（ADR-0030 第 3 點第 6 步）。刪不掉時不影響結果。</summary>
    public static void RemovePackage(string packagePath)
    {
        try
        {
            File.Delete(packagePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>無法套用：移到 rejected\，旁邊寫「&lt;檔名&gt;.原因.txt」。傳回移過去的路徑；移不動時為 null。</summary>
    public static string? Reject(ProgramFolder folder, string packagePath, string reason)
    {
        try
        {
            Directory.CreateDirectory(folder.RejectedFolder);
            var name = Path.GetFileNameWithoutExtension(packagePath);
            var ext = Path.GetExtension(packagePath);
            var dest = Path.Combine(folder.RejectedFolder, name + ext);
            for (int i = 2; File.Exists(dest); i++) dest = Path.Combine(folder.RejectedFolder, $"{name} ({i}){ext}");
            File.Move(packagePath, dest);
            File.WriteAllText(dest + ".原因.txt", $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n{reason}\r\n");
            return dest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    static string Mb(long bytes) => $"{bytes / (1024.0 * 1024):N0} MB";
}

/// <summary>CRC-32（zip 使用的 IEEE 802.3 多項式）。</summary>
internal static class Crc32
{
    public const uint Initial = 0xFFFFFFFF;

    static readonly uint[] Table = Build();

    static uint[] Build()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    public static uint Finish(uint crc) => crc ^ 0xFFFFFFFF;
}
