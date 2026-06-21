using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using TheKrystalShip.Rag.Index;
using TheKrystalShip.Rag.Indexing;

namespace TheKrystalShip.Rag.Tests;

/// <summary>
/// Smoke test for the OS-bound half of the daemon: a real <see cref="FileSystemWatcher"/> over a
/// temp dir, a fake embedder injected so no Ollama is needed. The coalescing/serialization logic is
/// covered deterministically by <see cref="CoalescingRebuildLoopTests"/>; here we only prove the
/// wiring — an initial build lands, and a later file change drives a re-index — polling within a
/// generous timeout rather than racing FileSystemWatcher's inherently asynchronous delivery.
/// </summary>
public sealed class IndexWatcherTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "kgsm-rag-watch-" + Guid.NewGuid().ToString("N"));
    private readonly string _indexPath;

    public IndexWatcherTests()
    {
        Directory.CreateDirectory(_dir);
        // Index lives OUTSIDE the watched dir so writing it can never self-trigger the watcher.
        _indexPath = _dir + ".krag";
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
        try { File.Delete(_indexPath); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task It_builds_initially_then_re_indexes_when_a_source_file_appears()
    {
        File.WriteAllText(Path.Combine(_dir, "a.md"), "alpha original content");
        var embedder = new RecordingEmbeddingClient(dimension: 4);
        var watcher = new IndexWatcher(
            embedder,
            new IndexBuilderOptions { Sources = [_dir] },
            _indexPath,
            TimeSpan.FromMilliseconds(50),
            NullLoggerFactory.Instance);

        using var cts = new CancellationTokenSource();
        var run = watcher.RunAsync(cts.Token);
        try
        {
            // Initial build (the watcher signals itself once on startup).
            await WaitUntilAsync(idx => idx.Manifest.Count == 1);

            // A new source file → a debounced re-index that includes it.
            File.WriteAllText(Path.Combine(_dir, "b.md"), "bravo new content");
            await WaitUntilAsync(idx => idx.Manifest.Count == 2);
        }
        finally
        {
            cts.Cancel();
            await run;
        }
    }

    /// <summary>Polls the on-disk index until <paramref name="predicate"/> holds or the timeout elapses
    /// (tolerant of FileSystemWatcher + debounce latency); fails the test if it never does.</summary>
    private async Task WaitUntilAsync(Func<RagIndex, bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (RagIndexFile.TryReadFromFile(_indexPath, out var index) && predicate(index))
                return;
            await Task.Delay(25);
        }

        // One last read for a clear failure message.
        RagIndexFile.TryReadFromFile(_indexPath, out var final)
            .Should().BeTrue("the watcher should have written a readable index within the timeout");
        predicate(final!).Should().BeTrue("the produced index should match the predicate within the timeout");
    }
}
