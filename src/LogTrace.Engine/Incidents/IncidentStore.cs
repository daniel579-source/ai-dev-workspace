using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LogTrace.Engine.Incidents;

/// <summary>讀取時沒有使用的檔案：檔名與原因。</summary>
public sealed record IncidentIssue(string FileName, string Reason);

/// <summary>讀到的紀錄、檔案位置，以及讀取時檔案內容的雜湊（存檔前用來檢查是否被別人改過）。</summary>
public sealed record StoredIncident(IncidentRecord Record, string Path, string Stamp);

/// <summary>存檔時發現檔案在讀取之後已被修改（例如另一台電腦），沒有覆寫。</summary>
public sealed class IncidentConflictException(string id)
    : Exception($"異常紀錄 {id} 在讀取之後已被修改（可能是另一台電腦），沒有存檔；請重新載入後再修改。")
{
    public string Id { get; } = id;
}

/// <summary>
/// 異常紀錄的存放（ADR-0016 第 4 點）：廠別設定檔旁邊的 <c>&lt;廠別設定檔名&gt;.incidents\</c> 資料夾，每筆一個 JSON 檔（候選格式），
/// 檔名為紀錄編號。屬於廠別的本機資料，不進 repo。
/// 存檔前比對讀取時的內容雜湊，檔案已被別人修改時不覆寫（使用者，2026-11-13）；先寫暫存檔再取代。
/// </summary>
public sealed class IncidentStore(string folder)
{
    public const string FolderExtension = ".incidents";
    public const string FileExtension = ".json";

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 中文直接寫出，方便閱讀
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public string Folder { get; } = folder;

    /// <summary>廠別設定檔旁邊的異常紀錄資料夾：同一資料夾，名稱為設定檔名（不含 .json）加 .incidents。</summary>
    public static string FolderBeside(string plantConfigPath) =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(plantConfigPath) ?? "", System.IO.Path.GetFileNameWithoutExtension(plantConfigPath) + FolderExtension);

    public string PathOf(string id) => System.IO.Path.Combine(Folder, id + FileExtension);

    public static string Serialize(IncidentRecord record) => JsonSerializer.Serialize(record, Options);

    /// <summary>讀取 JSON；格式版本較新、不是 JSON 或缺少編號時拋出 FormatException。</summary>
    public static IncidentRecord Deserialize(string json)
    {
        int version;
        try
        {
            using var doc = JsonDocument.Parse(json);
            version = doc.RootElement.TryGetProperty("formatVersion", out var v) && v.TryGetInt32(out var n) ? n : 0;
        }
        catch (JsonException ex)
        {
            throw new FormatException("不是有效的 JSON：" + ex.Message, ex);
        }
        if (version < 1) throw new FormatException("缺少格式版本");
        if (version > IncidentRecord.CurrentFormatVersion) throw new FormatException($"格式版本 {version} 較新，請更新程式");
        try
        {
            var record = JsonSerializer.Deserialize<IncidentRecord>(json, Options) ?? throw new FormatException("內容是空的");
            if (string.IsNullOrWhiteSpace(record.Id)) throw new FormatException("缺少紀錄編號");
            return record;
        }
        catch (JsonException ex)
        {
            throw new FormatException("內容有誤：" + ex.Message, ex);
        }
    }

    /// <summary>讀取資料夾中的所有紀錄（依編號）；資料夾不存在時傳回空清單。讀不了的檔案不使用，列出檔名與原因。</summary>
    public (IReadOnlyList<StoredIncident> Records, IReadOnlyList<IncidentIssue> Issues) LoadAll()
    {
        var records = new List<StoredIncident>();
        var issues = new List<IncidentIssue>();
        if (!Directory.Exists(Folder)) return (records, issues);
        foreach (var path in Directory.EnumerateFiles(Folder, "*" + FileExtension).Order(StringComparer.OrdinalIgnoreCase))
        {
            var name = System.IO.Path.GetFileName(path);
            try
            {
                var bytes = File.ReadAllBytes(path);
                var record = Deserialize(Decode(bytes));
                if (!string.Equals(record.Id + FileExtension, name, StringComparison.OrdinalIgnoreCase))
                    issues.Add(new(name, $"檔名與紀錄編號 {record.Id} 不一致"));
                else records.Add(new(record, path, StampOf(bytes)));
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                issues.Add(new(name, ex.Message));
            }
        }
        return (records, issues);
    }

    /// <summary>
    /// 存檔。新紀錄 expectedStamp 為 null，檔案已存在時視為衝突；既有紀錄的檔案內容與讀取時不同（或已被刪除）時視為衝突。
    /// 衝突時拋出 IncidentConflictException，不覆寫。傳回存檔後的資訊。
    /// </summary>
    public StoredIncident Save(IncidentRecord record, string? expectedStamp)
    {
        var path = PathOf(record.Id);
        string? current = File.Exists(path) ? StampOf(File.ReadAllBytes(path)) : null;
        if (current != expectedStamp) throw new IncidentConflictException(record.Id);

        Directory.CreateDirectory(Folder);
        var bytes = new UTF8Encoding(false).GetBytes(Serialize(record));
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
        return new(record, path, StampOf(bytes));
    }

    static string Decode(byte[] bytes) =>
        new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes.AsSpan().StartsWith((byte[])[0xEF, 0xBB, 0xBF]) ? bytes[3..] : bytes);

    static string StampOf(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
