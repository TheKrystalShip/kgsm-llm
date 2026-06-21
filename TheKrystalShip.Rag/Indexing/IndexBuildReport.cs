namespace TheKrystalShip.Rag.Indexing;

/// <summary>
/// What one build did, for the host to log. <see cref="FilesEmbedded"/> + <see cref="FilesReused"/>
/// = files in the new index; <see cref="FilesReused"/> is the incremental win (unchanged files whose
/// chunks were carried over from the previous index, not re-embedded). <see cref="FilesRemoved"/> were
/// in the previous manifest but are gone from the sources now.
/// </summary>
public sealed record IndexBuildReport(
    int SourceFiles,
    int FilesEmbedded,
    int FilesReused,
    int FilesRemoved,
    int TotalChunks,
    int ChunksEmbedded);
