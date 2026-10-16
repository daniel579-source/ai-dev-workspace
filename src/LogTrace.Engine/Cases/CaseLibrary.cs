using System.Text.Json;
using LogTrace.Engine.Plants;
using LogTrace.Engine.Storage;

namespace LogTrace.Engine.Cases;

/// <summary>案件清單中的一個案件。</summary>
public sealed record CaseSummary(string Folder, string Name, DateTimeOffset CreatedAt, DateTime LastWriteTime);

/// <summary>
/// 案件存放位置（ADR-0011 第 3 點）：預設 %LOCALAPPDATA%\LogTrace\Cases，可以修改。
/// 刪除案件後案件資料庫就不存在，所以建立與刪除另外記在存放位置下的 audit-log.jsonl（ADR-0009 第 5 點）。
/// </summary>
public sealed class CaseLibrary(string root)
{
    public const string AuditLogFileName = "audit-log.jsonl";

    public string Root { get; } = Path.GetFullPath(root);

    public static string DefaultRoot => Path.Combine(AppDataRoot, "Cases");

    /// <summary>程式的本機資料夾：%LOCALAPPDATA%\LogTrace。</summary>
    public static string AppDataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LogTrace");

    /// <summary>以案件名稱建立新的案件資料夾；名稱重複時加上序號。</summary>
    public Case Create(string name, PlantConfig plant)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("請輸入案件名稱", nameof(name));
        Directory.CreateDirectory(Root);
        var baseName = SafeFolderName(name.Trim());
        var folder = Path.Combine(Root, baseName);
        for (int i = 2; Directory.Exists(folder); i++) folder = Path.Combine(Root, $"{baseName} ({i})");
        var c = Case.Create(folder, name.Trim(), plant);
        AppendAudit(AuditEntry.Now("建立案件", folder, name.Trim()));
        return c;
    }

    /// <summary>存放位置下的所有案件（含 case.db 的子資料夾）；打不開的資料夾略過。</summary>
    public IReadOnlyList<CaseSummary> List()
    {
        if (!Directory.Exists(Root)) return [];
        var list = new List<CaseSummary>();
        foreach (var dir in Directory.GetDirectories(Root))
        {
            var db = Path.Combine(dir, Case.DatabaseFileName);
            if (!File.Exists(db)) continue;
            try
            {
                using var store = new SqliteEventStore(db, readOnly: true);
                var info = store.LoadInfo();
                list.Add(new CaseSummary(dir, info.Name, info.CreatedAt, File.GetLastWriteTime(db)));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or Microsoft.Data.Sqlite.SqliteException)
            {
                // 版本不符或損壞的案件不列出；資料夾保留，不自動刪除（ADR-0009 第 6 點）
            }
        }
        return list.OrderBy(c => c.Name, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>
    /// 刪除案件資料夾（ADR-0009 第 6 點）。只刪除案件中的複本，使用者原本的 LOG 不受影響。
    /// 呼叫前要先關閉案件；只接受存放位置下、含 case.db 的資料夾。
    /// </summary>
    public void Delete(string caseFolder)
    {
        var folder = Path.GetFullPath(caseFolder).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(folder), Root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"只能刪除存放位置下的案件：{folder}");
        if (!File.Exists(Path.Combine(folder, Case.DatabaseFileName)))
            throw new InvalidOperationException($"不是案件資料夾：{folder}");

        foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(folder, recursive: true);
        AppendAudit(AuditEntry.Now("刪除案件", folder));
    }

    /// <summary>存放位置的稽核紀錄（建立與刪除案件）。</summary>
    public IReadOnlyList<AuditEntry> GetAudit()
    {
        var path = Path.Combine(Root, AuditLogFileName);
        if (!File.Exists(path)) return [];
        return File.ReadAllLines(path).Where(l => l.Length > 0)
            .Select(l => JsonSerializer.Deserialize<AuditEntry>(l)!).ToList();
    }

    void AppendAudit(AuditEntry entry)
    {
        Directory.CreateDirectory(Root);
        File.AppendAllText(Path.Combine(Root, AuditLogFileName), JsonSerializer.Serialize(entry) + "\n");
    }

    static string SafeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars).TrimEnd('.', ' ');
    }
}
