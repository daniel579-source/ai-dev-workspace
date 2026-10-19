using System.Security.Cryptography;

namespace LogTrace.Engine.Cases;

/// <summary>搬移的結果：已搬移、目的地已有同名項目而略過、搬移失敗（舊位置保留原狀）。</summary>
public sealed record RelocationResult(IReadOnlyList<string> Moved, IReadOnlyList<string> Skipped, IReadOnlyList<(string Name, string Error)> Failed)
{
    public bool AllMoved => Skipped.Count == 0 && Failed.Count == 0;
}

/// <summary>
/// 把舊存放位置的案件或廠別設定搬到新位置（#57，ADR-0011 第 3 點）。
/// 案件內含原始 LOG 的複本，不能遺失：每個項目先複製，逐檔比對大小與 SHA-256 後，才刪除舊位置的項目；
/// 任何一步失敗時刪除不完整的複本，舊位置保留原狀。目的地已有同名項目時不覆蓋，略過並回報。
/// </summary>
public static class DataRelocation
{
    /// <summary>資料夾存在且有任何檔案或子資料夾。</summary>
    public static bool HasData(string folder) =>
        Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any();

    /// <summary>兩個位置是否相同（不分大小寫，忽略結尾的分隔符號）。</summary>
    public static bool SameFolder(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    public static RelocationResult Move(string from, string to)
    {
        var moved = new List<string>();
        var skipped = new List<string>();
        var failed = new List<(string, string)>();
        Directory.CreateDirectory(to);
        foreach (var source in Directory.EnumerateFileSystemEntries(from).ToList())
        {
            var name = Path.GetFileName(source);
            var target = Path.Combine(to, name);
            if (File.Exists(target) || Directory.Exists(target))
            {
                skipped.Add(name);
                continue;
            }
            try
            {
                CopyVerified(source, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(target);
                failed.Add((name, ex.Message));
                continue;
            }
            try
            {
                if (Directory.Exists(source)) Directory.Delete(source, recursive: true);
                else File.Delete(source);
                moved.Add(name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 新位置已有完整且核對過的複本；舊位置刪不掉時只回報，不再動新位置
                failed.Add((name, "已複製到新位置，但舊位置無法刪除：" + ex.Message));
            }
        }
        if (Directory.Exists(from) && !Directory.EnumerateFileSystemEntries(from).Any()) Directory.Delete(from);
        return new(moved, skipped, failed);
    }

    static void CopyVerified(string source, string target)
    {
        if (File.Exists(source))
        {
            CopyFile(source, target);
            return;
        }
        Directory.CreateDirectory(target);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            CopyFile(file, Path.Combine(target, Path.GetRelativePath(source, file)));
    }

    static void CopyFile(string source, string target)
    {
        File.Copy(source, target);
        if (new FileInfo(source).Length != new FileInfo(target).Length || !Hash(source).SequenceEqual(Hash(target)))
            throw new IOException($"複製後內容不一致：{source}");
    }

    static byte[] Hash(string path)
    {
        using var s = File.OpenRead(path);
        return SHA256.HashData(s);
    }

    static void TryDelete(string target)
    {
        try
        {
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            else if (File.Exists(target)) File.Delete(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 不完整的複本刪不掉時保留；舊位置的資料不受影響
        }
    }
}
