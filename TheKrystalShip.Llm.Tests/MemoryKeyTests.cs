using FluentAssertions;

using TheKrystalShip.Llm.Conversation;

namespace TheKrystalShip.Llm.Tests;

/// <summary>
/// <see cref="MemoryKey"/>: the slug a memory is filed under. Normalisation is what makes rewriting a
/// memory supersede it — two spellings of the same intent have to land on one key, or the model
/// reaching for something it wrote last week files a duplicate instead.
/// </summary>
public sealed class MemoryKeyTests
{
    [Theory]
    [InlineData("factorio-for-tests", "factorio-for-tests")]
    [InlineData("Factorio for tests", "factorio-for-tests")]
    [InlineData("factorio_for_tests", "factorio-for-tests")]
    [InlineData("Factorio  for   tests", "factorio-for-tests")]
    [InlineData("factorio, for tests!", "factorio-for-tests")]
    [InlineData("  Factorio For Tests  ", "factorio-for-tests")]
    public void Sanitize_CollapsesSpellingsOfOneIntentOntoOneKey(string raw, string expected) =>
        MemoryKey.Sanitize(raw).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    [InlineData("---")]
    public void Sanitize_NothingUsable_IsNull(string? raw) =>
        MemoryKey.Sanitize(raw).Should().BeNull();

    [Fact]
    public void Sanitize_CapsLength_WithNoTrailingSeparator()
    {
        var key = MemoryKey.Sanitize(new string('a', 40) + " " + new string('b', 40));
        key.Should().NotBeNull();
        key!.Length.Should().BeLessThanOrEqualTo(MemoryKey.MaxLength);
        key.Should().NotEndWith("-");
    }

    [Fact]
    public void Sanitize_IsIdempotent()
    {
        // A key read back out of the store and passed through again must not change, or a rewrite
        // would file under a different key than the one it was shown.
        var once = MemoryKey.Sanitize("Prefers Factorio for tests")!;
        MemoryKey.Sanitize(once).Should().Be(once);
    }
}
