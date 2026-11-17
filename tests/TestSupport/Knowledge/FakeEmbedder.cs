using LogTrace.Engine.Knowledge;

namespace LogTrace.Tests.Support;

/// <summary>
/// 測試用的 embedding（#171）：不連 Ollama。每個字元依字碼放進 64 維中的一格後正規化，共同字元越多越相似。
/// 記錄每次送出的文字，用來檢查只重算改過的條目。
/// </summary>
public sealed class FakeEmbedder(string model = "fake", string version = "v1") : IEmbedder
{
    public string Model => model;
    public string Version { get; set; } = version;
    public bool Available { get; set; } = true;
    public List<string> Embedded { get; } = [];

    public Task<EmbedderStatus> CheckAsync(CancellationToken ct = default) =>
        Task.FromResult(Available ? new EmbedderStatus(true, Version, "") : new EmbedderStatus(false, "", "連不到 Ollama（測試）"));

    public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        Embedded.AddRange(texts);
        return Task.FromResult(texts.Select(Vector).ToArray());
    }

    static float[] Vector(string text)
    {
        var v = new float[64];
        foreach (var c in text.Where(c => !char.IsWhiteSpace(c))) v[c % 64] += 1;
        return Vectors.Normalize(v);
    }
}
