namespace Raffaello.Core.Coding;

/// <summary>
/// Character n-gram TF-IDF index with cosine similarity (no external service). Documents are normalised descriptions;
/// an inverted index keeps queries fast over the 30k-row E-Promise list.
/// </summary>
public sealed class TextIndex
{
    private readonly int _n;
    private readonly List<Dictionary<string, double>> _docs = new();
    private readonly List<double> _norms = new();
    private readonly Dictionary<string, List<int>> _postings = new(StringComparer.Ordinal);
    private Dictionary<string, double> _idf = new(StringComparer.Ordinal);
    private bool _built;

    public TextIndex(int n = 3) => _n = n;
    public int Count => _docs.Count;

    public static IEnumerable<string> Grams(string text, int n)
    {
        var t = " " + Fingerprints.NormalizeDescription(text) + " ";
        for (var i = 0; i + n <= t.Length; i++) yield return t.Substring(i, n);
        // word tokens weigh in as well (sizes like "4X10", "25MM" matter more than their trigrams)
        foreach (var w in Fingerprints.Tokens(text)) yield return "#" + w;
    }

    /// <summary>Adds a document; returns its index.</summary>
    public int Add(string text)
    {
        var tf = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var g in Grams(text, _n)) tf[g] = tf.GetValueOrDefault(g) + 1;
        _docs.Add(tf);
        _built = false;
        return _docs.Count - 1;
    }

    private void Build()
    {
        var df = new Dictionary<string, int>(StringComparer.Ordinal);
        _postings.Clear();
        for (var d = 0; d < _docs.Count; d++)
            foreach (var g in _docs[d].Keys)
            {
                df[g] = df.GetValueOrDefault(g) + 1;
                if (!_postings.TryGetValue(g, out var list)) _postings[g] = list = new List<int>();
                list.Add(d);
            }
        var n = Math.Max(1, _docs.Count);
        _idf = df.ToDictionary(kv => kv.Key, kv => Math.Log((1.0 + n) / (1.0 + kv.Value)) + 1.0, StringComparer.Ordinal);
        _norms.Clear();
        foreach (var d in _docs)
        {
            double s = 0;
            foreach (var (g, c) in d) { var w = (1 + Math.Log(c)) * _idf[g]; s += w * w; }
            _norms.Add(Math.Sqrt(s));
        }
        _built = true;
    }

    /// <summary>Top-k documents by cosine similarity (0..1).</summary>
    public List<(int Doc, double Score)> Search(string query, int k = 10)
    {
        if (!_built) Build();
        if (_docs.Count == 0) return new();
        var q = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var g in Grams(query, _n)) q[g] = q.GetValueOrDefault(g) + 1;
        var qw = new Dictionary<string, double>(StringComparer.Ordinal);
        double qn = 0;
        foreach (var (g, c) in q)
        {
            if (!_idf.TryGetValue(g, out var idf)) continue;
            var w = (1 + Math.Log(c)) * idf;
            qw[g] = w; qn += w * w;
        }
        qn = Math.Sqrt(qn);
        if (qn <= 0) return new();
        var acc = new Dictionary<int, double>();
        foreach (var (g, w) in qw)
            foreach (var d in _postings[g])
            {
                var dw = (1 + Math.Log(_docs[d][g])) * _idf[g];
                acc[d] = acc.GetValueOrDefault(d) + w * dw;
            }
        return acc.Select(kv => (kv.Key, kv.Value / (qn * _norms[kv.Key])))
            .OrderByDescending(x => x.Item2).Take(k).ToList();
    }
}
