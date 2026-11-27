using System.Text;
using System.Text.Json.Nodes;

namespace TechValidation.LlmQuery;

/// <summary>提示詞與 JSON schema（ADR-0013 第 5 點：LLM 只把句子轉成搜尋條件，條件顯示在畫面上讓工程師修改）。</summary>
public static class Prompt
{
    public static readonly string[] Modules = ["EXE", "PLC", "Server"];
    public static readonly string[] Levels = ["ERROR", "WARN", "INFO", "DEBUG"];

    /// <summary>解析欄位的種類與說明（LogTrace.Engine.Parsing.FieldKinds 的子集）。</summary>
    public static readonly (string Kind, string Description)[] Kinds =
    [
        ("conv", "Conv（輸送設備）編號，例如 1203、A-12"),
        ("vehicle", "搬送機編號，例如 SSCA01、SRCA02"),
        ("rm", "RM（堆高起重機）編號，例如 RM01"),
        ("elevator", "電梯編號，例如 EV01"),
        ("eq", "EQ（其他公司的設備）編號，例如 9001"),
        ("fireDoor", "防火門編號，例如 FD03"),
        ("command", "命令編號，例如 CMD-0042"),
        ("pallet", "棧板編號，例如 PLT00123"),
        ("bin", "桶編號，例如 BIN0456"),
        ("storage", "儲位編號（7 碼數字），例如 0201503"),
        ("signal", "訊號名稱，例如 X0103"),
        ("alarmCode", "警報代碼，例如 E-501"),
        ("bcr", "條碼讀取器名稱，例如 BCR01"),
        ("barcode", "條碼內容，例如 BC778812"),
        ("scale", "磅秤編號，例如 SC01"),
    ];

    public static string System(string caseDate)
    {
        var sb = new StringBuilder();
        sb.AppendLine("你負責把倉儲設備 LOG 回溯工具使用者的問題，轉成搜尋條件。只輸出一個 JSON 物件，不要任何說明。");
        sb.AppendLine($"案件日期是 {caseDate}，時間一律寫成 \"{caseDate}THH:mm:ss\"（當地時間）。");
        sb.AppendLine("JSON 的欄位（沒有提到的欄位不要寫）：");
        sb.AppendLine("- allOf：每一個都要出現的關鍵字（字串陣列）。「而且」「同時」「有」的關鍵字放這裡。");
        sb.AppendLine("- anyOf：出現其中一個就可以的關鍵字（字串陣列）。「或」「其中之一」放這裡。");
        sb.AppendLine("- noneOf：不能出現的關鍵字（字串陣列）。「不要」「排除」「不包含」「不是」放這裡。");
        sb.AppendLine("- from、to：時間區間的起點與終點。「A 到 B」寫 from=A、to=B；「A 以後」只寫 from；「A 以前」只寫 to。");
        sb.AppendLine($"- modules：模組，只能是 {string.Join("、", Modules)}。");
        sb.AppendLine($"- levels：等級，只能是 {string.Join("、", Levels)}。");
        sb.AppendLine("- fields：設備與資料的編號，陣列的每一項是 {\"kind\": 種類, \"value\": 編號}。種類只能是：");
        foreach (var (kind, desc) in Kinds) sb.AppendLine($"  - {kind}：{desc}");
        sb.AppendLine("規則：");
        sb.AppendLine("1. 編號一律放 fields，不要放進關鍵字。關鍵字照使用者的原文寫，不要翻譯、不要自己加同義詞。");
        sb.AppendLine("2. 問題如果不是在找 LOG 事件（例如問原因、怎麼處理、是不是壞了、要寫報告、問目前的數量），只輸出 {\"unsupported\": true}，不要編造條件。");
        return sb.ToString();
    }

    public static string Answer(JsonNode? expect) =>
        expect is JsonValue v && v.TryGetValue<string>(out var s) && s == "unsupported" ? "{\"unsupported\": true}" : expect!.ToJsonString();

    /// <summary>以模型的對話格式組出提示詞（few-shot 範例當作前面的對話）。</summary>
    public static string Build(ModelFamily family, string system, IReadOnlyList<(string Question, string Answer)> examples, string question)
    {
        var sb = new StringBuilder();
        switch (family)
        {
            case ModelFamily.Phi:
                sb.Append($"<|system|>{system}<|end|>");
                foreach (var (q, a) in examples) sb.Append($"<|user|>{q}<|end|><|assistant|>{a}<|end|>");
                sb.Append($"<|user|>{question}<|end|><|assistant|>");
                break;
            case ModelFamily.Qwen:
                sb.Append($"<|im_start|>system\n{system}<|im_end|>\n");
                foreach (var (q, a) in examples)
                    sb.Append($"<|im_start|>user\n{q}<|im_end|>\n<|im_start|>assistant\n<think>\n\n</think>\n\n{a}<|im_end|>\n");
                // 不使用思考模式：直接給空的 <think> 區塊（Qwen3 的 enable_thinking=false 寫法）
                sb.Append($"<|im_start|>user\n{question}<|im_end|>\n<|im_start|>assistant\n<think>\n\n</think>\n\n");
                break;
        }
        return sb.ToString();
    }

    /// <summary>限制輸出的 JSON schema（guidance）：搜尋條件或 {"unsupported": true}。</summary>
    public static string Schema(string caseDate)
    {
        JsonObject StrArray() => new() { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } };
        JsonObject EnumArray(IEnumerable<string> values) =>
            new() { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(values.Select(v => (JsonNode)v).ToArray()) } };
        var time = new JsonObject { ["type"] = "string", ["pattern"] = $"^{caseDate}T[0-2][0-9]:[0-5][0-9]:[0-5][0-9]$" };
        var query = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["allOf"] = StrArray(), ["anyOf"] = StrArray(), ["noneOf"] = StrArray(),
                ["from"] = time.DeepClone(), ["to"] = time.DeepClone(),
                ["modules"] = EnumArray(Modules), ["levels"] = EnumArray(Levels),
                ["fields"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Kinds.Select(k => (JsonNode)k.Kind).ToArray()) },
                            ["value"] = new JsonObject { ["type"] = "string" },
                        },
                        ["required"] = new JsonArray("kind", "value"),
                        ["additionalProperties"] = false,
                    },
                },
            },
            ["additionalProperties"] = false,
        };
        var unsupported = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["unsupported"] = new JsonObject { ["type"] = "boolean", ["const"] = true } },
            ["required"] = new JsonArray("unsupported"),
            ["additionalProperties"] = false,
        };
        return new JsonObject { ["anyOf"] = new JsonArray(unsupported, query) }.ToJsonString();
    }
}

public enum ModelFamily { Phi, Qwen }
