using System.Net;
using System.Text;
using LogTrace.Engine.Knowledge;
using LogTrace.Tests.Support;

namespace LogTrace.Engine.Tests;

// 向量搜尋（#171，ADR-0022）：向量檔、只重算改過的條目、前幾名、Ollama 的回應。文字全部是虛構的；不連真的 Ollama。
public sealed class VectorSearchTests : IDisposable
{
    readonly TempFolder _tmp = new();

    public void Dispose() => _tmp.Dispose();

    string VecPath => Path.Combine(_tmp.Path, "範例廠.kb.vec");

    static readonly string[] Texts = ["E101 搬送機行走馬達過載 檢查減速機", "E301 光電感測器遮斷逾時 清潔感測器", "E303 條碼讀取失敗 清潔鏡頭"];

    [Fact]
    public void 向量檔寫入後讀回內容相同()
    {
        var items = new Dictionary<string, float[]> { ["a"] = [0.6f, 0.8f], ["b"] = [1f, 0f] };
        new VectorFile { Model = "bge-m3", Version = "sha256:abc", Dimensions = 2, Items = items }.Save(VecPath);

        var back = VectorFile.Load(VecPath)!;
        Assert.Equal("bge-m3", back.Model);
        Assert.Equal("sha256:abc", back.Version);
        Assert.Equal(2, back.Dimensions);
        Assert.Equal(items["a"], back.Items["a"]);
        Assert.Equal(items["b"], back.Items["b"]);
        Assert.False(File.Exists(VecPath + ".tmp"));
    }

    [Fact]
    public void 向量檔不存在或損毀時傳回null()
    {
        Assert.Null(VectorFile.Load(VecPath));
        File.WriteAllText(VecPath, "不是向量檔");
        Assert.Null(VectorFile.Load(VecPath));

        new VectorFile { Model = "m", Version = "v", Dimensions = 2, Items = new Dictionary<string, float[]> { ["a"] = [1f, 0f] } }.Save(VecPath);
        var bytes = File.ReadAllBytes(VecPath);
        File.WriteAllBytes(VecPath, bytes[..^2]); // 少了最後幾個位元組
        Assert.Null(VectorFile.Load(VecPath));
    }

    [Fact]
    public async Task 第一次搜尋建立向量_第二次不重算()
    {
        var e = new FakeEmbedder();

        var first = await SimilarSearch.FindAsync(e, "光電 遮斷", Texts, VecPath);
        Assert.Equal(3, first.Embedded);
        Assert.True(File.Exists(VecPath));

        e.Embedded.Clear();
        var second = await SimilarSearch.FindAsync(e, "光電 遮斷", Texts, VecPath);
        Assert.Equal(0, second.Embedded);
        Assert.Equal(["光電 遮斷"], e.Embedded); // 只送出查詢文字
        Assert.Equal(first.Indices, second.Indices);
    }

    [Fact]
    public async Task 只重算改過或新增的條目_刪除的條目從向量檔移除()
    {
        var e = new FakeEmbedder();
        await SimilarSearch.EnsureAsync(e, "v1", Texts, VecPath);
        e.Embedded.Clear();

        string[] changed = [Texts[0], "E301 光電感測器遮斷逾時 清潔感測器並確認配線", "E305 下游滿載 等待清空"];
        var (vectors, embedded) = await SimilarSearch.EnsureAsync(e, "v1", changed, VecPath);

        Assert.Equal(2, embedded);
        Assert.Equal([changed[1], changed[2]], e.Embedded);
        Assert.Equal(3, vectors.Length);
        Assert.Equal(3, VectorFile.Load(VecPath)!.Items.Count); // 舊的 E301、E303 已移除
    }

    [Fact]
    public async Task 模型版本改變時全部重算()
    {
        var e = new FakeEmbedder();
        await SimilarSearch.EnsureAsync(e, "v1", Texts, VecPath);
        e.Embedded.Clear();

        var (_, embedded) = await SimilarSearch.EnsureAsync(e, "v2", Texts, VecPath);

        Assert.Equal(3, embedded);
        Assert.Equal("v2", VectorFile.Load(VecPath)!.Version);
    }

    [Fact]
    public async Task 依相似度由高到低傳回前幾名()
    {
        var e = new FakeEmbedder();

        var r = await SimilarSearch.FindAsync(e, "條碼讀取失敗", Texts, VecPath, top: 2);

        Assert.Null(r.Unavailable);
        Assert.Equal(2, r.Indices.Count);
        Assert.Equal(2, r.Indices[0]); // E303 條碼讀取失敗
    }

    [Fact]
    public async Task 沒有關鍵字或沒有條目時不搜尋()
    {
        var e = new FakeEmbedder();

        Assert.Empty((await SimilarSearch.FindAsync(e, "  ", Texts, VecPath)).Indices);
        Assert.Empty((await SimilarSearch.FindAsync(e, "光電", [], VecPath)).Indices);
        Assert.Empty(e.Embedded);
        Assert.False(File.Exists(VecPath));
    }

    [Fact]
    public async Task 無法使用時傳回原因_不建立向量檔()
    {
        var e = new FakeEmbedder { Available = false };

        var r = await SimilarSearch.FindAsync(e, "光電", Texts, VecPath);

        Assert.Empty(r.Indices);
        Assert.Contains("連不到 Ollama", r.Unavailable);
        Assert.False(File.Exists(VecPath));
    }

    [Fact]
    public void 知識庫條目的文字與向量檔位置()
    {
        Assert.Equal("E101 虛構說明 虛構處置", SimilarSearch.KnowledgeText(new KnowledgeEntry(2, "E101", "虛構說明", "虛構處置", "虛構手冊")));
        Assert.Equal("E102 虛構說明", SimilarSearch.KnowledgeText(new KnowledgeEntry(3, "E102", "虛構說明", "", "")));
        Assert.Equal(Path.Combine("D:", "Plants", "範例廠.kb.vec"), SimilarSearch.KnowledgeVectorPath(Path.Combine("D:", "Plants", "範例廠.kb.csv")));
    }

    // ---- OllamaEmbedder：以假的 HTTP 回應測試，不連真的 Ollama ----

    sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content is not null) Bodies.Add(await request.Content.ReadAsStringAsync(ct));
            return respond(request);
        }
    }

    static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Ollama_有模型時可以使用並傳回版本()
    {
        var http = new HttpClient(new FakeHandler(_ => Json("""{"models":[{"name":"bge-m3:latest","model":"bge-m3:latest","digest":"sha256:abc"}]}""")));

        var s = await new OllamaEmbedder(http: http).CheckAsync();

        Assert.True(s.Available);
        Assert.Equal("sha256:abc", s.Version);
    }

    [Fact]
    public async Task Ollama_沒有模型或連不到時不能使用()
    {
        var none = new HttpClient(new FakeHandler(_ => Json("""{"models":[{"name":"llama3:latest","digest":"x"}]}""")));
        var s1 = await new OllamaEmbedder(http: none).CheckAsync();
        Assert.False(s1.Available);
        Assert.Contains("沒有模型 bge-m3", s1.Message);

        var down = new HttpClient(new FakeHandler(_ => throw new HttpRequestException("refused")));
        var s2 = await new OllamaEmbedder(http: down).CheckAsync();
        Assert.False(s2.Available);
        Assert.Contains("連不到 Ollama", s2.Message);
    }

    [Fact]
    public async Task Ollama_分批送出並正規化向量()
    {
        var handler = new FakeHandler(req =>
        {
            var body = req.Content!.ReadAsStringAsync().Result;
            var n = System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("input").GetArrayLength();
            return Json("""{"embeddings":[""" + string.Join(",", Enumerable.Repeat("[3,4]", n)) + "]}");
        });
        var texts = Enumerable.Range(0, 40).Select(i => $"虛構文字{i}").ToList();

        var v = await new OllamaEmbedder(http: new HttpClient(handler)).EmbedAsync(texts);

        Assert.Equal(40, v.Length);
        Assert.Equal([0.6f, 0.8f], v[0]);
        Assert.Equal(2, handler.Bodies.Count); // 32 + 8
        Assert.Contains("\"model\":\"bge-m3\"", handler.Bodies[0]);
    }

    [Fact]
    public async Task Ollama_回應錯誤時丟出例外_搜尋改為無法使用()
    {
        var handler = new FakeHandler(req => req.RequestUri!.AbsolutePath.EndsWith("/api/tags")
            ? Json("""{"models":[{"name":"bge-m3","digest":"d"}]}""")
            : new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("model failed") });

        var r = await SimilarSearch.FindAsync(new OllamaEmbedder(http: new HttpClient(handler)), "光電", Texts, VecPath);

        Assert.Empty(r.Indices);
        Assert.Contains("500", r.Unavailable);
    }
}
