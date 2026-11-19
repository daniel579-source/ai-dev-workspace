using System.Buffers.Binary;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LogTrace.Engine.Knowledge;

/// <summary>embedding 模型目前能不能用：可以用時附模型的版本（向量檔記錄它，版本不同時全部重算）。</summary>
public sealed record EmbedderStatus(bool Available, string Version, string Message);

/// <summary>把文字轉成向量（ADR-0022）。實作只連本機，不連外網。</summary>
public interface IEmbedder
{
    /// <summary>模型名稱（記錄在向量檔中）。</summary>
    string Model { get; }

    /// <summary>檢查服務與模型；不能用時 Available 為 false，Message 說明原因。不丟例外。</summary>
    Task<EmbedderStatus> CheckAsync(CancellationToken ct = default);

    /// <summary>每段文字一個向量（已正規化為長度 1）。</summary>
    Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}

/// <summary>
/// 經本機 Ollama 的 /api/embed 產生向量（ADR-0022 第 2 點）。預設 http://127.0.0.1:11434、模型 bge-m3（第 1 點 2026-11-17 補充）。
/// 只送知識庫條目與異常紀錄的文字到本機服務。
/// </summary>
public sealed class OllamaEmbedder(string model = OllamaEmbedder.DefaultModel, Uri? endpoint = null, HttpClient? http = null) : IEmbedder
{
    public const string DefaultModel = "bge-m3";
    public static readonly Uri DefaultEndpoint = new("http://127.0.0.1:11434/");

    /// <summary>一次送出的文字數（避免單一請求過大）。</summary>
    const int Batch = 32;

    readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    readonly Uri _endpoint = endpoint ?? DefaultEndpoint;

    public string Model => model;

    sealed record Tags(List<TagModel>? Models);
    sealed record TagModel(string? Name, string? Model, string? Digest);
    sealed record EmbedRequest(string Model, IReadOnlyList<string> Input);
    sealed record EmbedResponse(float[][]? Embeddings);

    public async Task<EmbedderStatus> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var tags = await _http.GetFromJsonAsync<Tags>(new Uri(_endpoint, "api/tags"), timeout.Token);
            var hit = tags?.Models?.FirstOrDefault(m => Same(m.Name) || Same(m.Model));
            return hit is null
                ? new(false, "", $"Ollama 中沒有模型 {model}")
                : new(true, hit.Digest ?? "", "");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            if (ct.IsCancellationRequested) throw;
            return new(false, "", $"連不到 Ollama（{_endpoint}）");
        }
    }

    bool Same(string? name) => name is not null && (name == model || name == model + ":latest");

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var all = new List<float[]>(texts.Count);
        for (int i = 0; i < texts.Count; i += Batch)
        {
            var chunk = texts.Skip(i).Take(Batch).ToList();
            using var res = await _http.PostAsJsonAsync(new Uri(_endpoint, "api/embed"), new EmbedRequest(model, chunk), ct);
            if (!res.IsSuccessStatusCode)
                throw new HttpRequestException($"Ollama 回應 {(int)res.StatusCode}：{await res.Content.ReadAsStringAsync(ct)}");
            var body = await res.Content.ReadFromJsonAsync<EmbedResponse>(ct);
            if (body?.Embeddings is not { } e || e.Length != chunk.Count) throw new HttpRequestException("Ollama 回應的向量數量不對");
            all.AddRange(e.Select(Vectors.Normalize));
        }
        return [.. all];
    }
}

/// <summary>向量的共用計算。</summary>
public static class Vectors
{
    public static float[] Normalize(float[] v)
    {
        double n = 0;
        foreach (var x in v) n += (double)x * x;
        if (n == 0) return v;
        var s = (float)(1 / Math.Sqrt(n));
        return v.Select(x => x * s).ToArray();
    }

    /// <summary>兩個已正規化的向量的 cosine 相似度（內積）。</summary>
    public static float Dot(float[] a, float[] b)
    {
        float s = 0;
        for (int i = 0; i < a.Length && i < b.Length; i++) s += a[i] * b[i];
        return s;
    }

    /// <summary>文字的雜湊（SHA-256，十六進位）：向量檔以它判斷條目是否改過。</summary>
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

/// <summary>
/// 向量檔（ADR-0022 第 4 點）：放在知識庫或異常紀錄旁的本機檔案，不進 repo、不存入案件。
/// 格式（自訂二進位，2026-11-17 使用者決定）：開頭 <c>LTVEC1\n</c>，接著 4 位元組（little-endian）的標頭長度與 UTF-8 JSON 標頭
/// （模型、版本、維度、每筆文字的雜湊），最後是 float32 陣列（筆數 × 維度，little-endian）。
/// </summary>
public sealed class VectorFile
{
    static readonly byte[] Magic = "LTVEC1\n"u8.ToArray();

    sealed record Header(string Model, string Version, int Dimensions, List<string> Keys);

    public required string Model { get; init; }
    public required string Version { get; init; }
    public required int Dimensions { get; init; }
    /// <summary>文字雜湊 → 向量。</summary>
    public required IReadOnlyDictionary<string, float[]> Items { get; init; }

    /// <summary>讀取向量檔；不存在、格式不對或內容損毀時傳回 null（呼叫端改為重算）。</summary>
    public static VectorFile? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < Magic.Length + 4 || !bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic)) return null;
            int at = Magic.Length;
            var headerLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at, 4));
            at += 4;
            if (headerLength < 0 || at + headerLength > bytes.Length) return null;
            var header = JsonSerializer.Deserialize<Header>(bytes.AsSpan(at, headerLength));
            at += headerLength;
            if (header is null || header.Dimensions <= 0 || header.Keys is null) return null;
            if (bytes.Length - at != (long)header.Keys.Count * header.Dimensions * 4) return null;
            var items = new Dictionary<string, float[]>(StringComparer.Ordinal);
            foreach (var key in header.Keys)
            {
                var v = new float[header.Dimensions];
                for (int d = 0; d < v.Length; d++, at += 4) v[d] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at, 4));
                items[key] = v;
            }
            return new() { Model = header.Model, Version = header.Version, Dimensions = header.Dimensions, Items = items };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>寫入向量檔：先寫暫存檔再取代，寫到一半中斷也不會留下壞檔。</summary>
    public void Save(string path)
    {
        var keys = Items.Keys.ToList();
        var header = JsonSerializer.SerializeToUtf8Bytes(new Header(Model, Version, Dimensions, keys));
        using var ms = new MemoryStream();
        ms.Write(Magic);
        Span<byte> four = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(four, header.Length);
        ms.Write(four);
        ms.Write(header);
        foreach (var key in keys)
            foreach (var x in Items[key])
            {
                BinaryPrimitives.WriteSingleLittleEndian(four, x);
                ms.Write(four);
            }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, ms.ToArray());
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>向量搜尋的結果：前幾名的索引（依相似度由高到低），或不能使用的原因。</summary>
public sealed record SimilarResult(IReadOnlyList<int> Indices, string? Unavailable, int Embedded)
{
    public static SimilarResult Off(string reason) => new([], reason, 0);
}

/// <summary>
/// 向量搜尋（ADR-0022）：對一組文字（知識庫條目或異常紀錄的現象）逐條計算 cosine 相似度，傳回前幾名。
/// 向量第一次搜尋時建立；之後只重算文字改過的項目，模型或版本改變時全部重算（2026-11-17 使用者決定）。
/// 只用來找出候選，顯示的仍是原文與出處（ADR-0015 第 5 點）。
/// </summary>
public static class SimilarSearch
{
    public const int DefaultTop = 5;

    public static async Task<SimilarResult> FindAsync(IEmbedder embedder, string query, IReadOnlyList<string> texts, string vectorPath,
        int top = DefaultTop, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || texts.Count == 0) return new([], null, 0);
        var status = await embedder.CheckAsync(ct);
        if (!status.Available) return SimilarResult.Off(status.Message);
        try
        {
            var (vectors, embedded) = await EnsureAsync(embedder, status.Version, texts, vectorPath, ct);
            var q = (await embedder.EmbedAsync([query.Trim()], ct))[0];
            var order = Enumerable.Range(0, texts.Count)
                .Select(i => (i, s: Vectors.Dot(q, vectors[i])))
                .OrderByDescending(x => x.s).ThenBy(x => x.i)
                .Take(top).Select(x => x.i).ToList();
            return new(order, null, embedded);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException or UnauthorizedAccessException)
        {
            if (ct.IsCancellationRequested) throw;
            return SimilarResult.Off(ex.Message);
        }
    }

    /// <summary>
    /// 取得每段文字的向量：沿用向量檔中雜湊相同的向量，只算新增或改過的；有算新的或有刪除的項目時寫回向量檔。
    /// 傳回各文字的向量與這次新算的筆數。
    /// </summary>
    public static async Task<(float[][] Vectors, int Embedded)> EnsureAsync(IEmbedder embedder, string version, IReadOnlyList<string> texts,
        string vectorPath, CancellationToken ct = default)
    {
        var file = VectorFile.Load(vectorPath);
        var cached = file is not null && file.Model == embedder.Model && file.Version == version ? file.Items : null;
        var keys = texts.Select(Vectors.Hash).ToList();
        var missing = keys.Select((k, i) => (k, i)).Where(x => cached is null || !cached.ContainsKey(x.k))
            .GroupBy(x => x.k).Select(g => g.First()).ToList();
        var fresh = missing.Count == 0 ? [] : await embedder.EmbedAsync(missing.Select(x => texts[x.i]).ToList(), ct);
        var items = new Dictionary<string, float[]>(StringComparer.Ordinal);
        for (int j = 0; j < missing.Count; j++) items[missing[j].k] = fresh[j];
        foreach (var k in keys.Distinct())
            if (!items.ContainsKey(k)) items[k] = cached![k];
        if (missing.Count > 0 || cached is null || cached.Count != items.Count)
        {
            var dims = items.Values.First().Length;
            new VectorFile { Model = embedder.Model, Version = version, Dimensions = dims, Items = items }.Save(vectorPath);
        }
        return (keys.Select(k => items[k]).ToArray(), missing.Count);
    }

    /// <summary>知識庫條目做 embedding 的文字：「代碼 說明 處置」（ADR-0022 第 1 點 2026-11-17 補充），後面接別名或俗稱（#190）；文字改變時向量依雜湊重算。</summary>
    public static string KnowledgeText(KnowledgeEntry e) => string.Join(" ", new[] { e.Code, e.Description, e.Action }.Concat(e.AliasList).Where(s => s.Length > 0));

    /// <summary>知識庫的向量檔：知識庫檔旁，副檔名 .kb.vec（例如 F18-ALine.kb.csv → F18-ALine.kb.vec）。</summary>
    public static string KnowledgeVectorPath(string knowledgePath) =>
        knowledgePath.EndsWith(KnowledgeBase.Extension, StringComparison.OrdinalIgnoreCase)
            ? knowledgePath[..^KnowledgeBase.Extension.Length] + ".kb.vec"
            : knowledgePath + ".vec";
}
