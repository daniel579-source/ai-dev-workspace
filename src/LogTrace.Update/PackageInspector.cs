using System.IO.Compression;
using System.Text.Json;

namespace LogTrace.Update;

/// <summary>檢查一個升級包的結果：Manifest 為 null 時 Problem 說明原因。</summary>
public sealed record PackageInspection(string Path, UpdateManifest? Manifest, string? Problem, long UncompressedBytes)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// 升級包的結構檢查（ADR-0030 第 3 點第 2 步）：完整的 zip、根目錄的 update.json、必要檔案都在、沒有跑出目的資料夾的路徑。
/// 不做來源驗證（第 2 點）。壓縮檔 CRC 在解開時逐一核對（<see cref="UpdateInstaller"/>），不完整就不切換。
/// </summary>
public static class PackageInspector
{
    public static PackageInspection Inspect(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var manifestEntry = zip.GetEntry(UpdateManifest.FileName);
            if (manifestEntry is null) return Fail(path, "不是 LogTrace 的升級包（根目錄沒有 update.json）");
            UpdateManifest manifest;
            using (var r = new StreamReader(manifestEntry.Open()))
                manifest = UpdateManifest.Parse(r.ReadToEnd());
            if (manifest.Problem() is { } problem) return Fail(path, problem);

            var prefix = manifest.PayloadPrefix;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long bytes = 0;
            foreach (var e in zip.Entries)
            {
                var name = e.FullName.Replace('\\', '/');
                if (!IsSafe(name)) return Fail(path, $"升級包中有不安全的路徑：{e.FullName}");
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || name.EndsWith('/')) continue;
                names.Add(name[prefix.Length..]);
                bytes += e.Length;
            }
            if (names.Count == 0) return Fail(path, $"升級包中沒有 {prefix} 資料夾的內容");
            var missing = manifest.Files.Where(f => !names.Contains(f.Replace('\\', '/'))).ToList();
            if (missing.Count > 0)
                return Fail(path, $"升級包缺少必要檔案：{string.Join("、", missing.Take(5))}{(missing.Count > 5 ? $" 等 {missing.Count} 個" : "")}");
            return new(path, manifest, null, bytes);
        }
        catch (InvalidDataException)
        {
            return Fail(path, "不是完整的 zip 檔（檔案損壞，或是還沒合併的分割檔）");
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return Fail(path, "update.json 的內容無法讀取：" + ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(path, "無法讀取升級包：" + ex.Message);
        }
    }

    /// <summary>相對路徑、不含 .. 與磁碟代號（避免解開到目的資料夾以外，ADR-0012 第 3 點）。</summary>
    internal static bool IsSafe(string name) =>
        name.Length > 0 && !name.StartsWith('/') && !name.Contains(':') && name.Split('/').All(p => p != "..");

    static PackageInspection Fail(string path, string problem) => new(path, null, problem, 0);
}
