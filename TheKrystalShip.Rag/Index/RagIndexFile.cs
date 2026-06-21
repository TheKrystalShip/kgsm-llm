using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace TheKrystalShip.Rag.Index;

/// <summary>
/// Binary reader/writer for the on-disk RAG index. Hand-rolled binary (not JSON) because the
/// payload is mostly contiguous floats: compact, allocation-light to scan, and reflection-free
/// (so it's Native-AOT clean without a serializer context). Layout:
/// <code>
///   "KRAG" (4 bytes)  int FormatVersion
///   string EmbeddingModel   int Dimension   int ChunkSize   int ChunkOverlap
///   int manifestCount   { string Path, string ContentHash, int ChunkOffset, int ChunkCount } *
///   int chunkCount      { string SourcePath, string HeaderPath, string Text, float[Dimension] } *
/// </code>
/// Strings are length-prefixed UTF-8 (<see cref="BinaryWriter.Write(string)"/>). The index is a
/// regenerable artifact written atomically (temp → same-dir rename), so corruption-hardening
/// beyond magic + version validation is unnecessary (plan §D8/§D9).
/// </summary>
public static class RagIndexFile
{
    private static readonly byte[] Magic = "KRAG"u8.ToArray();

    /// <summary>Writes <paramref name="index"/> to <paramref name="stream"/> (stream left open).</summary>
    public static void Write(Stream stream, RagIndex index)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(index);

        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        w.Write(Magic);
        w.Write(RagIndex.CurrentFormatVersion);
        w.Write(index.EmbeddingModel);
        w.Write(index.Dimension);
        w.Write(index.ChunkSize);
        w.Write(index.ChunkOverlap);

        w.Write(index.Manifest.Count);
        foreach (var e in index.Manifest)
        {
            w.Write(e.Path);
            w.Write(e.ContentHash);
            w.Write(e.ChunkOffset);
            w.Write(e.ChunkCount);
        }

        w.Write(index.Chunks.Count);
        foreach (var c in index.Chunks)
        {
            if (c.Vector.Length != index.Dimension)
                throw new RagIndexFormatException(
                    $"Chunk vector length {c.Vector.Length} does not match index dimension {index.Dimension}.");

            w.Write(c.SourcePath);
            w.Write(c.HeaderPath);
            w.Write(c.Text);
            foreach (var f in c.Vector)
                w.Write(f);
        }
    }

    /// <summary>Reads a <see cref="RagIndex"/> from <paramref name="stream"/> (stream left open).</summary>
    /// <exception cref="RagIndexFormatException">Bad magic or unsupported format version.</exception>
    public static RagIndex Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        var magic = r.ReadBytes(Magic.Length);
        if (!magic.AsSpan().SequenceEqual(Magic))
            throw new RagIndexFormatException("Not a KRAG index file (bad magic header).");

        var version = r.ReadInt32();
        if (version != RagIndex.CurrentFormatVersion)
            throw new RagIndexFormatException(
                $"Unsupported index format version {version} (this build reads {RagIndex.CurrentFormatVersion}); re-index from sources.");

        var model = r.ReadString();
        var dimension = r.ReadInt32();
        var chunkSize = r.ReadInt32();
        var chunkOverlap = r.ReadInt32();

        var manifestCount = r.ReadInt32();
        var manifest = new List<SourceFileEntry>(manifestCount);
        for (var i = 0; i < manifestCount; i++)
            manifest.Add(new SourceFileEntry(r.ReadString(), r.ReadString(), r.ReadInt32(), r.ReadInt32()));

        var chunkCount = r.ReadInt32();
        var chunks = new List<IndexedChunk>(chunkCount);
        for (var i = 0; i < chunkCount; i++)
        {
            var sourcePath = r.ReadString();
            var headerPath = r.ReadString();
            var text = r.ReadString();
            var vector = new float[dimension];
            for (var d = 0; d < dimension; d++)
                vector[d] = r.ReadSingle();
            chunks.Add(new IndexedChunk(sourcePath, headerPath, text, vector));
        }

        return new RagIndex
        {
            EmbeddingModel = model,
            Dimension = dimension,
            ChunkSize = chunkSize,
            ChunkOverlap = chunkOverlap,
            Chunks = chunks,
            Manifest = manifest,
        };
    }

    /// <summary>
    /// Writes the index to <paramref name="path"/> atomically: a temp file in the same directory
    /// (so the final move is a same-filesystem rename — atomic on POSIX) is fully written and
    /// flushed, then renamed over the target. A concurrent reader never sees a partial file.
    /// </summary>
    public static void WriteToFile(string path, RagIndex index)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(index);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("Path has no directory component.", nameof(path));
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write))
            {
                Write(fs, index);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* best-effort cleanup */ }
            }
            throw;
        }
    }

    /// <summary>Reads a <see cref="RagIndex"/> from a file.</summary>
    /// <exception cref="RagIndexFormatException">Bad magic or unsupported format version.</exception>
    public static RagIndex ReadFromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        return Read(fs);
    }

    /// <summary>
    /// Never-throw read for callers that treat "no usable existing index" as a normal state — the
    /// indexer loading the previous index for incremental reuse, where any failure (missing,
    /// unreadable, wrong magic/version, or a corrupt body) simply means "rebuild from scratch".
    /// Returns false (and a null <paramref name="index"/>) instead of throwing.
    /// </summary>
    public static bool TryReadFromFile(string path, [NotNullWhen(true)] out RagIndex? index)
    {
        index = null;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;

        try
        {
            index = ReadFromFile(path);
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or RagIndexFormatException or FormatException)
        {
            // FormatException covers a valid header over a corrupt body (a malformed 7-bit
            // string-length prefix), the one read failure outside the IO family.
            return false;
        }
    }
}
