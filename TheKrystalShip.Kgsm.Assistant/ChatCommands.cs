using TheKrystalShip.KGSM.Auth;

namespace TheKrystalShip.Kgsm.Assistant;

/// <summary>
/// One parameter of a chat command. <see cref="Values"/> is the fixed set an
/// <see cref="Autocomplete"/> option offers; null means the option takes free text.
/// </summary>
public sealed record ChatCommandOption(
    string Name,
    string? Description,
    string Type,
    bool Required,
    bool Autocomplete,
    IReadOnlyList<string>? Values = null);

/// <summary>
/// One command a person can type at the assistant, in the assistant's own words.
/// <para>
/// <see cref="Gate"/> is what this leaf requires of whoever runs it, from the ecosystem's ordered
/// tier — so a surface prints the same word that decides the answer everywhere else.
/// <see cref="Mutates"/> marks a command that changes stored state rather than reading it; no
/// command on this surface touches a game server, so the split separates the reference lookups from
/// the ones that write something.
/// </para>
/// </summary>
public sealed record ChatCommand(
    string Name,
    string Description,
    KgsmTier Gate,
    bool Mutates,
    IReadOnlyList<ChatCommandOption> Options);

/// <summary>
/// The commands the assistant answers to when they are typed at it, and the single source every
/// surface reads: the Service serves this catalog at <c>GET /commands</c> filtered to the caller's
/// tier, the build writes it to the shipped manifest the Control Panel renders, and the CLI REPL
/// dispatches from it. A command is declared once, here.
/// <para>
/// The <b>leaf executes everything it lists</b>. Nothing here is a client-side convention this leaf
/// is unaware of — which is what lets a surface treat the catalog as authoritative rather than
/// advisory.
/// </para>
/// <para>
/// Arguments are bare verbs or a fixed value list. Nothing takes free text and nothing takes a
/// server name: the standalone assistant surface has no server roster, and a command that worked on
/// one web surface but not the other would be exactly the divergence the shared chat code exists to
/// prevent.
/// </para>
/// </summary>
public static class ChatCommands
{
    /// <summary>The leaf id this catalog belongs to — its config descriptor's id and the manifest's filename stem.</summary>
    public const string LeafId = "assistant";

    /// <summary>Where these commands are typed. The panel prints it; it is not <c>discord</c>.</summary>
    public const string Surface = "chat";

    /// <summary>The manifest schema this catalog is written as: commands keyed by the gate that admits them.</summary>
    public const int SchemaVersion = 2;

    /// <summary>The value list an on/off switch offers, and the spelling every surface accepts.</summary>
    public const string On = "on";

    /// <summary>The "off" half of <see cref="On"/>.</summary>
    public const string Off = "off";

    // A switch left bare toggles, which is what the composer's buttons and the CLI's /think already do.
    // Naming the state is for saying it out loud, not for reaching a state you otherwise could not.
    private static ChatCommandOption StateOption(string what) => new(
        Name: "state",
        Description: $"Whether {what} is on. Leave it out to toggle.",
        Type: "string",
        Required: false,
        Autocomplete: true,
        Values: [On, Off]);

    /// <summary>The command list, in the order a surface shows them.</summary>
    public static readonly IReadOnlyList<ChatCommand> All =
    [
        new("help", "Show what you can type here", KgsmTier.Viewer, Mutates: false, Options: []),
        new("tools", "Show what the assistant can do for you", KgsmTier.Viewer, Mutates: false, Options: []),
        new("compact", "Summarize this conversation in place to free up context",
            KgsmTier.Viewer, Mutates: true, Options: []),
        new("new", "Start a fresh conversation", KgsmTier.Viewer, Mutates: true, Options: []),
        new("think", "Whether the assistant reasons step by step before answering",
            KgsmTier.Viewer, Mutates: true, Options: [StateOption("step-by-step reasoning")]),
        new("autorun", "Whether authorized actions run without asking you to confirm each one",
            KgsmTier.Admin, Mutates: true, Options: [StateOption("auto-run")]),
    ];

    /// <summary>
    /// The commands <paramref name="tier"/> may run. The tier ladder is ordered, so an admin sees
    /// every viewer command too. A command a caller cannot run is absent rather than listed and
    /// refused — a surface should not offer what it would then reject.
    /// </summary>
    public static IReadOnlyList<ChatCommand> For(KgsmTier tier) =>
        [.. All.Where(c => tier >= c.Gate)];

    /// <summary>
    /// The command <paramref name="name"/> names, or null when the catalog has none. Case-insensitive:
    /// a person typing <c>/Compact</c> means the command, and refusing it would be pedantry.
    /// </summary>
    public static ChatCommand? Find(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : All.FirstOrDefault(c => string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads a switch's argument: <see cref="On"/>/<see cref="Off"/> give that state, an absent or
    /// blank argument gives null meaning "toggle", and anything else is rejected so a typo does not
    /// silently pick a state the person did not ask for.
    /// </summary>
    public static bool TryReadState(string? argument, out bool? state)
    {
        state = null;
        if (string.IsNullOrWhiteSpace(argument))
            return true;

        switch (argument.Trim().ToLowerInvariant())
        {
            case On: state = true; return true;
            case Off: state = false; return true;
            default: return false;
        }
    }
}
