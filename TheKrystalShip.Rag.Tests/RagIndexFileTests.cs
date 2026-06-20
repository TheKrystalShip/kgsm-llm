using System.Text;

using FluentAssertions;

using TheKrystalShip.Rag.Index;

namespace TheKrystalShip.Rag.Tests;

public class RagIndexFileTests
{
    private static RagIndex SampleIndex() => new()
    {
        EmbeddingModel = "nomic-embed-text",
        Dimension = 3,
        ChunkSize = 2000,
        ChunkOverlap = 200,
        Chunks =
        [
            new IndexedChunk("a.md", "A > One", "alpha text", [1f, 2f, 3f]),
            new IndexedChunk("b.md", "", "beta text", [4f, 5f, 6f]),
        ],
        Manifest =
        [
            new SourceFileEntry("a.md", "hash-a", 0, 1),
            new SourceFileEntry("b.md", "hash-b", 1, 1),
        ],
    };

    [Fact]
    public void Write_then_read_round_trips_every_field()
    {
        var original = SampleIndex();

        using var stream = new MemoryStream();
        RagIndexFile.Write(stream, original);
        stream.Position = 0;
        var read = RagIndexFile.Read(stream);

        read.EmbeddingModel.Should().Be(original.EmbeddingModel);
        read.Dimension.Should().Be(original.Dimension);
        read.ChunkSize.Should().Be(original.ChunkSize);
        read.ChunkOverlap.Should().Be(original.ChunkOverlap);
        read.Manifest.Should().BeEquivalentTo(original.Manifest);
        read.Chunks.Should().HaveCount(2);
        read.Chunks[0].Should().BeEquivalentTo(original.Chunks[0]);
        read.Chunks[1].Vector.Should().Equal(4f, 5f, 6f);
    }

    [Fact]
    public void Read_rejects_a_file_with_bad_magic()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("NOPE....garbage"));

        var act = () => RagIndexFile.Read(stream);

        act.Should().Throw<RagIndexFormatException>().WithMessage("*magic*");
    }

    [Fact]
    public void Read_rejects_an_unsupported_format_version()
    {
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            w.Write("KRAG"u8.ToArray());
            w.Write(999); // a version this build doesn't understand → rebuild, never read old layout
        }
        stream.Position = 0;

        var act = () => RagIndexFile.Read(stream);

        act.Should().Throw<RagIndexFormatException>().WithMessage("*version 999*");
    }

    [Fact]
    public void WriteToFile_writes_atomically_and_leaves_no_temp_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rag-index-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "index.bin");

            RagIndexFile.WriteToFile(path, SampleIndex());

            File.Exists(path).Should().BeTrue();
            Directory.GetFiles(dir).Should().ContainSingle().Which.Should().Be(path);

            var read = RagIndexFile.ReadFromFile(path);
            read.Chunks.Should().HaveCount(2);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
