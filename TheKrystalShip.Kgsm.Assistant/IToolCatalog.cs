using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant;

/// <summary>
/// The tool definitions offered to the model, composed from the on-disk <c>tools.json</c> and the
/// tier membership in <see cref="LlmTools"/>.
/// <para>
/// The split is deliberate. Everything the model READS about a tool — its description, its parameter
/// prose, types and allowed values — is on disk, so routing is tuned by editing a file and taking
/// effect on the next turn. Which tier a tool belongs to is NOT: that decides who is offered it and
/// whether it is staged for confirmation, and a text file does not get to make that decision.
/// </para>
/// </summary>
public interface IToolCatalog
{
    /// <summary>Offered to every caller, authorized or not.</summary>
    IReadOnlyList<LlmToolDefinition> ReadOnly { get; }

    /// <summary>
    /// The ordinary-turn offer for an action-authorized caller. Excludes
    /// <see cref="LlmTools.ReviseBlueprint"/>, which is only offered beside an open draft.
    /// </summary>
    IReadOnlyList<LlmToolDefinition> All { get; }

    /// <summary>Appended to the offer only on a turn that carries an open blueprint draft.</summary>
    LlmToolDefinition ReviseBlueprintTool { get; }
}
