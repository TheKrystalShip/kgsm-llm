using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using TheKrystalShip.Kgsm.Assistant.Service.Security;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

/// <summary>
/// The key sign-in tokens are signed with, on a host that was handed no secret. Everyone signed in
/// stays signed in across a restart only because it is the same key each time, so what is pinned here
/// is that it survives, that a configured one still wins, and that the file it lives in is nobody
/// else's to read.
/// </summary>
public sealed class HostSigningKeyTests : IDisposable
{
    private readonly string _stateDir =
        Path.Combine(Path.GetTempPath(), $"kgsm-assistant-key-{Guid.NewGuid():N}");

    private string KeyPath => Path.Combine(_stateDir, "signing-key");

    private HostSigningKey Resolve(string? configured) =>
        new(configured, KeyPath, NullLogger<HostSigningKey>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_stateDir)) Directory.Delete(_stateDir, recursive: true);
    }

    [Fact]
    public void Generates_and_keeps_a_key_when_none_is_configured()
    {
        HostSigningKey key = Resolve(null);

        key.FilePath.Should().Be(KeyPath);
        File.ReadAllText(KeyPath).Should().Be(key.Value);
        // 48 random bytes, base64 — the shape the env example tells an operator to generate.
        Convert.FromBase64String(key.Value).Should().HaveCount(48);
    }

    [Fact]
    public void The_generated_key_file_is_readable_by_nobody_else()
    {
        Resolve("");

        File.GetUnixFileMode(KeyPath).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public void A_second_start_reads_the_same_key()
    {
        // The whole point: a restart does not sign everybody out.
        Resolve(null).Value.Should().Be(Resolve(null).Value);
    }

    [Fact]
    public void A_key_written_by_hand_is_used_verbatim()
    {
        Directory.CreateDirectory(_stateDir);
        // With a trailing newline, because a file an operator wrote has one and a key that silently
        // included it would validate none of the tokens minted before they edited it.
        File.WriteAllText(KeyPath, "written-by-an-operator\n");

        Resolve(null).Value.Should().Be("written-by-an-operator");
    }

    [Fact]
    public void A_configured_key_wins_and_nothing_is_written()
    {
        HostSigningKey key = Resolve("a-real-host-was-given-a-secret");

        key.Value.Should().Be("a-real-host-was-given-a-secret");
        key.FilePath.Should().BeNull();
        File.Exists(KeyPath).Should().BeFalse();
    }
}
