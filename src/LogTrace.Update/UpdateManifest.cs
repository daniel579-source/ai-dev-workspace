using System.Text.Json;
using System.Text.Json.Serialization;

namespace LogTrace.Update;

/// <summary>
/// 升級包根目錄的 update.json（ADR-0030 第 2 點）。啟動程式只依這個檔案辨識升級包，不靠檔名。
/// <list type="bullet">
/// <item>程式包：Kind = app，Version 為程式版本，RuntimeVersion 為需要的 WebView2 Runtime 版本；內容在 app\ 下。</item>
/// <item>Runtime 包：Kind = runtime，Version 與 RuntimeVersion 都是 Runtime 版本；內容在 runtime\ 下。</item>
/// </list>
/// Files 是必要檔案清單（相對於 app\ 或 runtime\）；Notes 是該版的更新說明（程式包才有）。
/// </summary>
public sealed record UpdateManifest(string Kind, string Version, string RuntimeVersion, string[] Files, string Notes)
{
    public const string FileName = "update.json";
    public const string AppKind = "app";
    public const string RuntimeKind = "runtime";

    public bool IsApp => Kind == AppKind;

    public bool IsRuntime => Kind == RuntimeKind;

    /// <summary>內容在升級包中的資料夾（結尾有 /）。</summary>
    public string PayloadPrefix => IsApp ? "app/" : "runtime/";

    /// <summary>程式版本（程式包才有意義）。</summary>
    public Version AppVersion => System.Version.Parse(Version);

    public static UpdateManifest Parse(string json) =>
        JsonSerializer.Deserialize(json, UpdateJson.Default.UpdateManifest) ?? throw new FormatException("update.json 是空的");

    public string ToJson() => JsonSerializer.Serialize(this, UpdateJson.Default.UpdateManifest);

    /// <summary>內容有誤時傳回原因。</summary>
    public string? Problem()
    {
        if (!IsApp && !IsRuntime) return $"update.json 的種類「{Kind}」不是 app 或 runtime";
        if (string.IsNullOrWhiteSpace(Version)) return "update.json 沒有版本";
        if (IsApp && !System.Version.TryParse(Version, out _)) return $"update.json 的版本「{Version}」格式不正確";
        if (string.IsNullOrWhiteSpace(RuntimeVersion)) return "update.json 沒有 WebView2 Runtime 版本";
        if (RuntimeVersion.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "update.json 的版本含有不能用在資料夾名稱的字元";
        if (Files is null || Files.Length == 0) return "update.json 沒有必要檔案清單";
        return null;
    }

    public string Describe() => IsApp ? $"程式 {Version}" : $"WebView2 Runtime {Version}";
}

/// <summary>Program\launcher.json：預設啟動的版本（ADR-0030 第 1 點）。</summary>
public sealed record LauncherState(string Current);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UpdateManifest))]
[JsonSerializable(typeof(LauncherState))]
internal sealed partial class UpdateJson : JsonSerializerContext;
