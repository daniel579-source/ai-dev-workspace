using System.Diagnostics;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.NGram;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;
using TechValidation.Core;

namespace TechValidation.Bench;

/// <summary>
/// 比較對象 B：Lucene.NET 4.8（beta）。以 3-gram 建索引，詞以 trigram 片語查詢（精確子字串）。
/// 少於 3 個字的詞無法用索引，回報為不支援。
/// </summary>
public sealed class LuceneBackend(string path)
{
    const LuceneVersion V = LuceneVersion.LUCENE_48;
    public string Path { get; } = path;

    sealed class TrigramAnalyzer : Analyzer
    {
        protected override TokenStreamComponents CreateComponents(string fieldName, TextReader reader) =>
            new(new NGramTokenizer(V, reader, 3, 3));
    }

    public double Load(CaseStore store, IReadOnlyList<int> days)
    {
        if (System.IO.Directory.Exists(Path)) System.IO.Directory.Delete(Path, true);
        var sw = Stopwatch.StartNew();
        using var dir = FSDirectory.Open(Path);
        var cfg = new IndexWriterConfig(V, new TrigramAnalyzer()) { OpenMode = OpenMode.CREATE, RAMBufferSizeMB = 512 };
        using var w = new IndexWriter(dir, cfg);
        Parallel.ForEach(days, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(days.Count, Environment.ProcessorCount) }, d =>
        {
            var file = store.FlatFile(d);
            foreach (var (start, end) in FlatNormFile.Partitions(file, 1))
            {
                FlatNormFile.Scan(file, start, end, (seq, utf8) =>
                {
                    var doc = new Document
                    {
                        new Int32Field("day", d, Field.Store.NO),
                        new NumericDocValuesField("seq", seq),
                        new TextField("t", System.Text.Encoding.UTF8.GetString(utf8), Field.Store.NO),
                    };
                    w.AddDocument(doc);
                    return true;
                });
            }
        });
        w.ForceMerge(1);
        w.Commit();
        return sw.Elapsed.TotalSeconds;
    }

    sealed class SeqCollector : ICollector
    {
        public readonly List<long> Seqs = new();
        NumericDocValues? _dv;
        public bool AcceptsDocsOutOfOrder => true;
        public void SetScorer(Scorer scorer) { }
        public void SetNextReader(AtomicReaderContext context) => _dv = context.AtomicReader.GetNumericDocValues("seq");
        public void Collect(int doc) => Seqs.Add(_dv!.Get(doc));
    }

    static Query TermQ(string t)
    {
        if (t.Length == 3) return new TermQuery(new Term("t", t));
        var pq = new PhraseQuery();
        for (int i = 0; i + 3 <= t.Length; i++) pq.Add(new Term("t", t.Substring(i, 3)), i);
        return pq;
    }

    public SearchResult Search(SearchQuery q, IReadOnlyList<int> days)
    {
        q = q.Normalized();
        var res = new SearchResult { Engine = "Lucene.NET 3-gram" };
        if (q.Regex is not null || q.All.Concat(q.Any).Concat(q.Not).Any(t => t.Length < 3) || q.All.Length + q.Any.Length == 0)
        {
            res.Note = "不支援（少於 3 字、只有不包含、或正規表示式）";
            res.TotalMs = double.NaN;
            return res;
        }
        var sw = Stopwatch.StartNew();
        using var dir = FSDirectory.Open(Path);
        using var reader = DirectoryReader.Open(dir);
        var searcher = new IndexSearcher(reader);
        var bq = new BooleanQuery
        {
            { NumericRangeQuery.NewInt32Range("day", days.Min(), days.Max(), true, true), Occur.MUST },
        };
        foreach (var t in q.All) bq.Add(TermQ(t), Occur.MUST);
        if (q.Any.Length > 0)
        {
            var any = new BooleanQuery { MinimumNumberShouldMatch = 1 };
            foreach (var t in q.Any) any.Add(TermQ(t), Occur.SHOULD);
            bq.Add(any, Occur.MUST);
        }
        foreach (var t in q.Not) bq.Add(TermQ(t), Occur.MUST_NOT);
        var col = new SeqCollector();
        searcher.Search(bq, col);
        col.Seqs.Sort();
        res.Seqs.AddRange(col.Seqs);
        res.TotalMs = sw.Elapsed.TotalMilliseconds;
        res.FirstResultMs = res.Seqs.Count > 0 ? res.TotalMs : double.NaN;
        return res;
    }
}
