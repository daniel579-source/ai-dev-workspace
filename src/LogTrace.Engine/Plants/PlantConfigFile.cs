using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using LogTrace.Engine.Storage;

namespace LogTrace.Engine.Plants;

/// <summary>讀不了的廠別設定檔：格式版本較新或未知、不是 JSON、缺少必要欄位。</summary>
public sealed class PlantConfigFormatException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>設定檔內容有問題（例如別名重複）時無法存檔；Issues 指出有問題的元素。</summary>
public sealed class PlantConfigValidationException(IReadOnlyList<PlantConfigIssue> issues)
    : Exception("設定檔有問題，無法存檔：" + string.Join("；", issues.Select(i => i.Message)))
{
    public IReadOnlyList<PlantConfigIssue> Issues { get; } = issues;
}

/// <summary>設定檔的一個問題，以及相關元素的 id。</summary>
public sealed record PlantConfigIssue(string Message, IReadOnlyList<string> ElementIds);

/// <summary>
/// 廠別設定檔的讀寫與檢查（ADR-0006 第 2、7 點）。
/// <list type="bullet">
/// <item>讀取時先看格式版本：較新或未知的版本明確回報，不靜默讀錯。</item>
/// <item>存檔前檢查；有問題（例如別名重複）時不存檔。存檔時修訂號加 1，記錄修改人（作業系統帳號）與修改時間。</item>
/// <item>先寫到暫存檔再取代，寫到一半失敗時原檔不受影響。</item>
/// </list>
/// </summary>
public static class PlantConfigFile
{
    public const string Extension = ".json";

    static readonly int[] AllowedRotations = [0, 90, 180, 270];

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        // 中文與正規表示式中的 <、> 直接寫出，方便閱讀與比對（設定檔不會嵌入 HTML）
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false), new PropsConverter(), new GapThresholdsConverter() },
    };

    /// <summary>寫出 JSON；格式版本依內容決定（用到新功能時才升版，ADR-0006 第 7 點）。</summary>
    public static string Serialize(PlantConfigDocument doc)
    {
        doc.FormatVersion = doc.RequiredFormatVersion();
        return JsonSerializer.Serialize(doc, Options);
    }

    /// <summary>只寫出 Layout 元素（編輯器的剪貼簿與復原用，不寫進檔案）。</summary>
    public static string SerializeElements(IEnumerable<LayoutElement> elements) =>
        JsonSerializer.Serialize(elements.ToList(), Options);

    public static List<LayoutElement> DeserializeElements(string json) =>
        JsonSerializer.Deserialize<List<LayoutElement>>(json, Options) ?? [];

    public static PlantConfigDocument Deserialize(string json)
    {
        int version;
        try
        {
            using var parsed = JsonDocument.Parse(json);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object
                || !parsed.RootElement.TryGetProperty("formatVersion", out var v)
                || v.ValueKind != JsonValueKind.Number
                || !v.TryGetInt32(out version))
                throw new PlantConfigFormatException("這不是廠別設定檔：缺少格式版本（formatVersion）。");
        }
        catch (JsonException ex)
        {
            throw new PlantConfigFormatException("這不是有效的 JSON 檔：" + ex.Message, ex);
        }
        if (version > PlantConfigDocument.CurrentFormatVersion)
            throw new PlantConfigFormatException(
                $"這個設定檔的格式版本是 {version}，比程式支援的版本 {PlantConfigDocument.CurrentFormatVersion} 新。請改用較新版的 LogTrace 開啟。");
        if (version < 1)
            throw new PlantConfigFormatException($"不認得的格式版本：{version}。");

        try
        {
            return JsonSerializer.Deserialize<PlantConfigDocument>(json, Options)
                ?? throw new PlantConfigFormatException("設定檔是空的。");
        }
        catch (JsonException ex)
        {
            throw new PlantConfigFormatException("設定檔的內容有誤：" + ex.Message, ex);
        }
    }

    public static PlantConfigDocument Load(string path)
    {
        try
        {
            return Deserialize(File.ReadAllText(path));
        }
        catch (PlantConfigFormatException ex)
        {
            throw new PlantConfigFormatException($"{Path.GetFileName(path)}：{ex.Message}", ex.InnerException);
        }
    }

    /// <summary>檢查後存檔；修訂號加 1 並記錄修改人與時間。有問題時丟出 PlantConfigValidationException，檔案與文件都不變。</summary>
    public static void Save(PlantConfigDocument doc, string path)
    {
        var issues = Validate(doc);
        if (issues.Count > 0) throw new PlantConfigValidationException(issues);

        var (revision, by, at) = (doc.Revision, doc.RevisedBy, doc.RevisedAt);
        doc.Revision++;
        doc.RevisedBy = AuditEntry.CurrentUser;
        doc.RevisedAt = DateTimeOffset.Now;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, Serialize(doc), new System.Text.UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            (doc.Revision, doc.RevisedBy, doc.RevisedAt) = (revision, by, at);
            throw;
        }
    }

    /// <summary>存檔前的檢查：別名在整份設定中不可重複（大小寫視為相同）、id 不可重複、旋轉角度、輸送線的兩端與交接點。</summary>
    public static IReadOnlyList<PlantConfigIssue> Validate(PlantConfigDocument doc)
    {
        var issues = new List<PlantConfigIssue>();
        var elements = doc.Layout.Elements;
        string Label(LayoutElement e) => string.IsNullOrWhiteSpace(e.Name) ? e.Id : e.Name!;

        if (string.IsNullOrWhiteSpace(doc.Plant))
            issues.Add(new("請輸入廠別名稱。", []));

        if (doc.ModuleOrder.Any(string.IsNullOrWhiteSpace) || doc.ModuleOrder.Distinct(StringComparer.OrdinalIgnoreCase).Count() != doc.ModuleOrder.Count)
            issues.Add(new("模組順序中有空白或重複的模組。", []));
        if (doc.KnownLoggers.Any(string.IsNullOrWhiteSpace))
            issues.Add(new("已知 LOG 種類中有空白的名稱。", []));
        foreach (var rule in doc.Encodings)
        {
            if (string.IsNullOrWhiteSpace(rule.Logger))
                issues.Add(new("編碼設定中有沒有填 Logger 的規則。", []));
            else if (rule.Encoding is not (Parsing.LogEncoding.Utf8Name or Parsing.LogEncoding.Big5Name))
                issues.Add(new($"「{rule.Logger}」的編碼「{rule.Encoding}」不支援；只能是 UTF-8 或 Big5。", []));
        }
        foreach (var g in doc.Encodings.Where(r => !string.IsNullOrWhiteSpace(r.Logger))
                     .GroupBy(r => ((r.Module ?? "").ToUpperInvariant(), r.Logger.ToUpperInvariant())).Where(g => g.Count() > 1))
            issues.Add(new($"「{(g.First().Module is { } m ? m + " / " : "")}{g.First().Logger}」的編碼設定重複。", []));

        foreach (var (code, name) in doc.StationModeNames)
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name))
                issues.Add(new($"站點模式名稱中有沒有填代號或名稱的列（代號「{code}」）。", []));
        foreach (var g in doc.StationModeNames.Where(p => !string.IsNullOrWhiteSpace(p.Key))
                     .GroupBy(p => p.Key.Trim(), StringComparer.Ordinal).Where(g => g.Count() > 1))
            issues.Add(new($"站點模式代號「{g.Key}」重複。", []));

        foreach (var (module, seconds) in doc.GapThresholds)
        {
            if (string.IsNullOrWhiteSpace(module))
                issues.Add(new("沒有 LOG 的門檻中有沒有填模組的列。", []));
            if (seconds < 0)
                issues.Add(new($"沒有 LOG 的門檻「{module}」的秒數必須是 0 以上的整數（0 表示不標示）。", []));
        }
        foreach (var g in doc.GapThresholds.Where(p => !string.IsNullOrWhiteSpace(p.Key))
                     .GroupBy(p => p.Key.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            issues.Add(new($"沒有 LOG 的門檻中，模組「{g.Key}」重複。", []));
        // #86：只依模組名稱比對，填到 Logger 或元件名稱時門檻不會生效，所以必須是模組順序中的模組。
        var modules = new HashSet<string>(doc.ModuleOrder.Select(m => m.Trim()), StringComparer.OrdinalIgnoreCase);
        foreach (var module in doc.GapThresholds.Select(p => p.Key.Trim()).Where(m => m.Length > 0 && !modules.Contains(m)).Distinct(StringComparer.OrdinalIgnoreCase))
            issues.Add(new($"沒有 LOG 的門檻的模組「{module}」不在模組順序中，請從模組順序中選擇。", []));

        foreach (var pattern in doc.MaskPatterns)
            if (Search.TextMasker.Check(pattern) is { } problem) issues.Add(new(problem, []));

        foreach (var rule in doc.ParseRules)
        {
            if (string.IsNullOrWhiteSpace(rule.Name)) issues.Add(new("有沒有名稱的解析規則。", []));
            if (Parsing.FieldExtractor.Check(rule) is { } problem) issues.Add(new(problem, []));
        }

        foreach (var g in elements.GroupBy(e => e.Id, StringComparer.Ordinal).Where(g => g.Count() > 1))
            issues.Add(new($"元素 id「{g.Key}」重複。", [g.Key]));

        var aliasOwners = elements
            .SelectMany(e => (e.Aliases ?? []).Select(a => (Alias: a.Trim(), Element: e)))
            .GroupBy(x => x.Alias, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);
        foreach (var g in aliasOwners)
        {
            var owners = g.Select(x => x.Element).Distinct().ToList();
            issues.Add(new(
                owners.Count == 1
                    ? $"別名「{g.Key}」在「{Label(owners[0])}」中重複。"
                    : $"別名「{g.Key}」同時屬於 {string.Join("、", owners.Select(o => $"「{Label(o)}」"))}；同一個別名只能屬於一個元素。",
                owners.Select(o => o.Id).ToList()));
        }

        foreach (var e in elements)
        {
            if (e.Aliases is { Count: > 0 } && !LayoutElement.CanHaveAliases(e.Type))
                issues.Add(new($"「{Label(e)}」不能有別名；只有站點、搬送機、對接設備可以。", [e.Id]));
            if (e.Aliases?.Any(string.IsNullOrWhiteSpace) == true)
                issues.Add(new($"「{Label(e)}」有空白的別名。", [e.Id]));
            if (e.Rotation is { } r && !AllowedRotations.Contains(r))
                issues.Add(new($"「{Label(e)}」的旋轉角度 {r} 度不允許；只能是 0、90、180、270。", [e.Id]));
            if (e.Type == LayoutElementType.Conveyor)
            {
                LayoutElement? End(string? id) => elements.FirstOrDefault(x => x.Id == id && LayoutElement.CanBeConveyorEnd(x.Type));
                var (from, to) = (End(e.From), End(e.To));
                if (from is null || to is null)
                    issues.Add(new($"輸送線「{Label(e)}」的兩端必須是站點、對接設備或搬送機。", [e.Id]));
                else if ((from.Type == LayoutElementType.Vehicle && e.FromPoint is null) || (to.Type == LayoutElementType.Vehicle && e.ToPoint is null))
                    issues.Add(new($"輸送線「{Label(e)}」連到搬送機的那一端沒有交接點。", [e.Id]));
            }
            if (e.Props is { } props)
                foreach (var g in props.GroupBy(p => p.Key, StringComparer.Ordinal).Where(g => g.Count() > 1 || string.IsNullOrWhiteSpace(g.Key)))
                    issues.Add(new(string.IsNullOrWhiteSpace(g.Key)
                        ? $"「{Label(e)}」有沒有名稱的自訂屬性。"
                        : $"「{Label(e)}」的自訂屬性「{g.Key}」重複。", [e.Id]));
        }
        return issues;
    }

    /// <summary>自訂屬性以 JSON 物件保存（鍵＝值），並保持順序。</summary>
    sealed class PropsConverter : JsonConverter<List<KeyValuePair<string, string>>>
    {
        public override List<KeyValuePair<string, string>> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("自訂屬性必須是物件（鍵＝值）。");
            var list = new List<KeyValuePair<string, string>>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var key = reader.GetString()!;
                reader.Read();
                if (reader.TokenType != JsonTokenType.String) throw new JsonException($"自訂屬性「{key}」的值必須是文字。");
                list.Add(new(key, reader.GetString()!));
            }
            return list;
        }

        public override void Write(Utf8JsonWriter writer, List<KeyValuePair<string, string>> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (var (k, v) in value) writer.WriteString(k, v);
            writer.WriteEndObject();
        }
    }

    /// <summary>沒有 LOG 的門檻以 JSON 物件保存（模組＝秒數），並保持順序。</summary>
    sealed class GapThresholdsConverter : JsonConverter<List<KeyValuePair<string, int>>>
    {
        public override List<KeyValuePair<string, int>> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("沒有 LOG 的門檻必須是物件（模組＝秒數）。");
            var list = new List<KeyValuePair<string, int>>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var key = reader.GetString()!;
                reader.Read();
                if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var seconds))
                    throw new JsonException($"沒有 LOG 的門檻「{key}」的秒數必須是整數。");
                list.Add(new(key, seconds));
            }
            return list;
        }

        public override void Write(Utf8JsonWriter writer, List<KeyValuePair<string, int>> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (var (k, v) in value) writer.WriteNumber(k, v);
            writer.WriteEndObject();
        }
    }
}

/// <summary>
/// 廠別設定檔的存放位置：預設 D:\LogTrace\Plants（#57），每個廠別一個 .json。分享時直接複製檔案（ADR-0006 第 7 點）。
/// </summary>
public sealed class PlantConfigLibrary(string root)
{
    public string Root { get; } = Path.GetFullPath(root);

    /// <summary>存放位置下的設定檔路徑，依檔名排序。</summary>
    public IReadOnlyList<string> List() =>
        Directory.Exists(Root)
            ? Directory.GetFiles(Root, "*" + PlantConfigFile.Extension).Order(StringComparer.CurrentCultureIgnoreCase).ToList()
            : [];

    /// <summary>新設定檔的預設路徑：以廠別名稱當檔名；已存在時加上序號。</summary>
    public string SuggestPath(string plantName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string((string.IsNullOrWhiteSpace(plantName) ? "廠別" : plantName.Trim())
            .Select(c => invalid.Contains(c) ? '_' : c).ToArray()).TrimEnd('.', ' ');
        var path = Path.Combine(Root, name + PlantConfigFile.Extension);
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(Root, $"{name} ({i}){PlantConfigFile.Extension}");
        return path;
    }
}
