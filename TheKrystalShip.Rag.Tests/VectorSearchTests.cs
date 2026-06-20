using FluentAssertions;

using TheKrystalShip.Rag.Index;

namespace TheKrystalShip.Rag.Tests;

public class VectorSearchTests
{
    private static IndexedChunk Chunk(string id, params float[] vector) =>
        new(id + ".md", "", id, vector);

    [Fact]
    public void TopK_ranks_by_cosine_similarity_highest_first()
    {
        var chunks = new[]
        {
            Chunk("exact", 1f, 0f, 0f),
            Chunk("orthogonal", 0f, 1f, 0f),
            Chunk("close", 0.9f, 0.1f, 0f),
        };

        var hits = VectorSearch.TopK(chunks, [1f, 0f, 0f], k: 2);

        hits.Should().HaveCount(2);
        hits[0].Chunk.Text.Should().Be("exact");
        hits[1].Chunk.Text.Should().Be("close");
        hits[0].Score.Should().BeGreaterThan(hits[1].Score);
        hits[0].Score.Should().BeApproximately(1f, 1e-5f);
    }

    [Fact]
    public void TopK_clamps_to_available_chunks()
    {
        var chunks = new[] { Chunk("only", 1f, 1f) };

        VectorSearch.TopK(chunks, [1f, 1f], k: 10).Should().ContainSingle();
    }

    [Fact]
    public void TopK_returns_empty_for_nonpositive_k_or_empty_corpus()
    {
        VectorSearch.TopK([Chunk("x", 1f)], [1f], k: 0).Should().BeEmpty();
        VectorSearch.TopK([], [1f], k: 5).Should().BeEmpty();
    }

    [Fact]
    public void Zero_norm_vectors_score_zero_not_nan()
    {
        var hits = VectorSearch.TopK([Chunk("zero", 0f, 0f)], [1f, 0f], k: 1);

        hits.Should().ContainSingle();
        hits[0].Score.Should().Be(0f);
    }
}
