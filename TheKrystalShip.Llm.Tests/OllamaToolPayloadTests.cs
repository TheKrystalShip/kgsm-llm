using System.Text.Json;

using FluentAssertions;

using TheKrystalShip.Llm.Models;
using TheKrystalShip.Llm.Backends;
using TheKrystalShip.Llm.Backends.Ollama;

using Xunit;

namespace TheKrystalShip.Llm.Tests;

/// <summary>
/// Covers the JSON-schema the Ollama client emits for a tool's parameters — in
/// particular the optional <c>enum</c> constraint (<see cref="LlmToolParameter.AllowedValues"/>),
/// which is the small-model reliability lever for categorical params like a command verb.
/// Offline guard for what the live tool-calling tests exercise end-to-end.
/// </summary>
public class OllamaToolPayloadTests
{
    private static JsonElement Properties(LlmToolDefinition def) =>
        JsonSerializer.SerializeToElement(ToolSchema.BuildFunction(def))
            .GetProperty("function").GetProperty("parameters").GetProperty("properties");

    [Fact]
    public void Parameter_WithoutAllowedValues_OmitsEnum()
    {
        var def = LlmToolDefinition.Create(new Tool("t"), "desc",
            new LlmToolParameter("instance_name", "the server"));

        var prop = Properties(def).GetProperty("instance_name");

        prop.GetProperty("type").GetString().Should().Be("string");
        prop.GetProperty("description").GetString().Should().Be("the server");
        prop.TryGetProperty("enum", out _).Should().BeFalse("no AllowedValues → no enum constraint");
    }

    [Fact]
    public void Parameter_WithAllowedValues_EmitsEnumConstraintInOrder()
    {
        var def = LlmToolDefinition.Create(new Tool("server_command"), "desc",
            new LlmToolParameter("verb", "the action",
                AllowedValues: new[] { "start", "stop", "restart", "update", "backup" }));

        var values = Properties(def).GetProperty("verb")
            .GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToArray();

        values.Should().Equal("start", "stop", "restart", "update", "backup");
    }

    [Fact]
    public void Required_ListsOnlyRequiredParameters()
    {
        var def = LlmToolDefinition.Create(new Tool("t"), "desc",
            new LlmToolParameter("instance_name", "required"),
            new LlmToolParameter("verb", "optional", Required: false));

        var required = JsonSerializer.SerializeToElement(ToolSchema.BuildFunction(def))
            .GetProperty("function").GetProperty("parameters").GetProperty("required")
            .EnumerateArray().Select(e => e.GetString()).ToArray();

        required.Should().Equal("instance_name");
    }
}
