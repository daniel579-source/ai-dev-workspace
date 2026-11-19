using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LogTrace.Engine.Flows;

/// <summary>樣板檔已被別人修改（或刪除），不覆寫。</summary>
public sealed class FlowConflictException() : Exception("樣板檔已被別人修改，請重新載入後再操作");

/// <summary>
/// 正常流程樣板檔的讀寫（ADR-0024，#192）：放在廠別設定檔旁（<c>&lt;設定檔名&gt;.flows.json</c>），JSON；
/// 存檔前比對讀取時的內容，先寫暫存檔再取代。匯出檔與樣板檔同一個格式。
/// 視同廠別專屬資料：只在本機與經授權的工程師電腦之間以檔案傳遞（ADR-0024 第 11 點）。
/// </summary>
public static class FlowStore
{
    public const string Extension = ".flows.json";

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false, // 樣本多時檔案較小
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string PathBeside(string plantConfigPath) =>
        Path.Combine(Path.GetDirectoryName(plantConfigPath) ?? "", Path.GetFileNameWithoutExtension(plantConfigPath) + Extension);

    /// <summary>讀取樣板檔；不存在時傳回 (null, null)。格式較新或內容有誤時拋出 FormatException。</summary>
    public static (FlowFile? File, string? Stamp) Load(string path)
    {
        if (!File.Exists(path)) return (null, null);
        var bytes = File.ReadAllBytes(path);
        return (Parse(bytes), StampOf(bytes));
    }

    public static FlowFile Parse(byte[] bytes)
    {
        try
        {
            using (var doc = JsonDocument.Parse(bytes))
            {
                var version = doc.RootElement.TryGetProperty("formatVersion", out var v) && v.TryGetInt32(out var n) ? n : 0;
                if (version < 1) throw new FormatException("不是正常流程樣板檔（缺少格式版本）");
                if (version > FlowFile.CurrentFormatVersion) throw new FormatException($"格式版本 {version} 較新，請更新程式");
            }
            return JsonSerializer.Deserialize<FlowFile>(bytes, Options) ?? throw new FormatException("內容是空的");
        }
        catch (JsonException ex)
        {
            throw new FormatException("內容有誤：" + ex.Message, ex);
        }
    }

    /// <summary>存檔；檔案內容與讀取時不同時拋出 FlowConflictException，不覆寫。傳回新的內容指紋。</summary>
    public static string Save(string path, FlowFile file, string? expectedStamp)
    {
        string? current = File.Exists(path) ? StampOf(File.ReadAllBytes(path)) : null;
        if (current != expectedStamp) throw new FlowConflictException();
        var bytes = Serialize(file);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
        return StampOf(bytes);
    }

    /// <summary>匯出成一個檔案（覆寫目的地）。</summary>
    public static void Export(string path, FlowFile file) => File.WriteAllBytes(path, Serialize(file));

    static byte[] Serialize(FlowFile file) =>
        new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(file with { FormatVersion = FlowFile.CurrentFormatVersion }, Options));

    static string StampOf(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
