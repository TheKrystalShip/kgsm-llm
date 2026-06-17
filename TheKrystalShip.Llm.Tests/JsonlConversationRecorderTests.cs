using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using TheKrystalShip.Llm.Models;
using TheKrystalShip.Llm.Ollama;
using TheKrystalShip.Llm.Recording;

namespace TheKrystalShip.Llm.Tests;

/// <summary>
/// The JSONL sink: one self-flushing line per turn in a daily <c>yyyy-MM-dd.jsonl</c> file, shaped
/// for grep/jq analysis, appended (never overwritten), and failure-isolated.
/// </summary>
public sealed class JsonlConversationRecorderTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "kgsm-rec-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private JsonlConversationRecorder Make(string? dir = null) => new(
        Options.Create(new RecordingOptions { Enabled = true, Directory = dir ?? _dir }),
        Options.Create(new OllamaOptions { Model = "gemma4:12b", Temperature = 0.3 }),
        NullLogger<JsonlConversationRecorder>.Instance);

    private static ConversationTurnRecord Rec(
        TurnOutcome outcome = TurnOutcome.Ok,
        string? final = "Terraria is up",
        DateTimeOffset? at = null)
    {
        var started = at ?? new DateTimeOffset(2026, 6, 17, 14, 2, 11, TimeSpan.Zero);
        return new ConversationTurnRecord
        {
            ConversationId = "cli:abc",
            StartedAt = started,
            CompletedAt = started.AddMilliseconds(210),
            UserPrompt = "is terraria up?",
            SystemPromptHash = "a1b2c3d4",
            Tools = new[]
            {
                new RecordedToolCall(
                    "get_status",
                    new Dictionary<string, string?> { ["instance"] = "terraria" },
                    "running, 3h12m",
                    210),
            },
            Iterations = 1,
            Outcome = outcome,
            Final = final,
            Usage = new LlmUsage(1720, 130, 32768),
        };
    }

    [Fact]
    public void Enabled_TracksDirectoryPresence()
    {
        Make().Enabled.Should().BeTrue();

        var noDir = new JsonlConversationRecorder(
            Options.Create(new RecordingOptions { Enabled = true, Directory = "" }),
            Options.Create(new OllamaOptions()),
            NullLogger<JsonlConversationRecorder>.Instance);
        noDir.Enabled.Should().BeFalse();   // nothing to write to ⇒ inert
    }

    [Fact]
    public void Record_WritesDailyFile_WithExpectedShape()
    {
        Make().Record(Rec());

        var file = Path.Combine(_dir, "2026-06-17.jsonl");
        File.Exists(file).Should().BeTrue();

        var lines = File.ReadAllLines(file);
        lines.Should().ContainSingle();

        using var doc = JsonDocument.Parse(lines[0]);
        var root = doc.RootElement;
        root.GetProperty("conv").GetString().Should().Be("cli:abc");
        root.GetProperty("user").GetString().Should().Be("is terraria up?");
        root.GetProperty("outcome").GetString().Should().Be("ok");
        root.GetProperty("capHit").GetBoolean().Should().BeFalse();
        root.GetProperty("iters").GetInt32().Should().Be(1);
        root.GetProperty("sysHash").GetString().Should().Be("a1b2c3d4");
        root.GetProperty("model").GetString().Should().Be("gemma4:12b");   // stamped from OllamaOptions
        root.GetProperty("final").GetString().Should().Be("Terraria is up");

        var usage = root.GetProperty("usage");
        usage.GetProperty("prompt").GetInt32().Should().Be(1720);
        usage.GetProperty("ctx").GetInt32().Should().Be(32768);

        var tools = root.GetProperty("tools");
        tools.GetArrayLength().Should().Be(1);
        tools[0].GetProperty("name").GetString().Should().Be("get_status");
        tools[0].GetProperty("result").GetString().Should().Be("running, 3h12m");
        tools[0].GetProperty("args").GetProperty("instance").GetString().Should().Be("terraria");
    }

    [Fact]
    public void Record_Appends_NeverOverwrites()
    {
        var r = Make();
        r.Record(Rec());
        r.Record(Rec());

        File.ReadAllLines(Path.Combine(_dir, "2026-06-17.jsonl")).Should().HaveCount(2);
    }

    [Fact]
    public void CancelledTurn_SerializesNullFinal()
    {
        Make().Record(Rec(outcome: TurnOutcome.Cancelled, final: null));

        var line = File.ReadAllLines(Path.Combine(_dir, "2026-06-17.jsonl"))[0];
        using var doc = JsonDocument.Parse(line);
        doc.RootElement.GetProperty("outcome").GetString().Should().Be("cancelled");
        doc.RootElement.GetProperty("final").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void SeparateDays_GoToSeparateFiles()
    {
        var r = Make();
        r.Record(Rec(at: new DateTimeOffset(2026, 6, 17, 23, 59, 0, TimeSpan.Zero)));
        r.Record(Rec(at: new DateTimeOffset(2026, 6, 18, 0, 1, 0, TimeSpan.Zero)));

        File.Exists(Path.Combine(_dir, "2026-06-17.jsonl")).Should().BeTrue();
        File.Exists(Path.Combine(_dir, "2026-06-18.jsonl")).Should().BeTrue();
    }

    [Fact]
    public void Record_SwallowsWriteFailure()   // a corpus write must never take down a turn
    {
        Directory.CreateDirectory(_dir);
        var notADir = Path.Combine(_dir, "occupied");
        File.WriteAllText(notADir, "x");   // a FILE where the recorder expects a directory

        var recorder = Make(dir: notADir);
        var act = () => recorder.Record(Rec());

        act.Should().NotThrow();
    }
}
