namespace TheKrystalShip.Rag.Index;

/// <summary>One retrieval hit: the matched chunk and its cosine similarity to the query (-1..1).</summary>
public sealed record SearchHit(IndexedChunk Chunk, float Score);

/// <summary>
/// Brute-force cosine top-k over a flat list of chunk vectors. For a personal docs corpus
/// (a few thousand chunks) this is sub-millisecond and allocation-light — an ANN index
/// (sqlite-vec/Qdrant) is the scale-up path, not the start (plan §D4).
/// </summary>
public static class VectorSearch
{
    /// <summary>
    /// Returns the <paramref name="k"/> chunks most similar to <paramref name="query"/> by cosine
    /// similarity, highest first. Zero-norm vectors score 0. A non-positive <paramref name="k"/>
    /// or empty corpus returns an empty list.
    /// </summary>
    public static IReadOnlyList<SearchHit> TopK(IReadOnlyList<IndexedChunk> chunks, float[] query, int k)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(query);

        if (k <= 0 || chunks.Count == 0)
            return [];

        var queryNorm = Norm(query);

        var hits = new List<SearchHit>(chunks.Count);
        foreach (var chunk in chunks)
            hits.Add(new SearchHit(chunk, Cosine(query, queryNorm, chunk.Vector)));

        // Descending by score; stable enough for retrieval (ties are rare with real embeddings).
        hits.Sort(static (a, b) => b.Score.CompareTo(a.Score));

        return k >= hits.Count ? hits : hits.GetRange(0, k);
    }

    private static float Cosine(float[] a, double aNorm, float[] b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException(
                $"Vector length mismatch: query {a.Length} vs chunk {b.Length}.");

        var bNorm = Norm(b);
        if (aNorm == 0 || bNorm == 0)
            return 0f;

        double dot = 0;
        for (var i = 0; i < a.Length; i++)
            dot += (double)a[i] * b[i];

        return (float)(dot / (aNorm * bNorm));
    }

    private static double Norm(float[] v)
    {
        double sum = 0;
        foreach (var x in v)
            sum += (double)x * x;
        return Math.Sqrt(sum);
    }
}
