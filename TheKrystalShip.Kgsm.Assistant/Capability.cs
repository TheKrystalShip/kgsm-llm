namespace TheKrystalShip.Kgsm.Assistant;

/// <summary>
/// What a tool DOES, as a stable identifier the code binds to — never the name the model sees.
/// <para>
/// A tool has two identities and they change on completely different schedules. The name is prose:
/// it is read by a model, it is tuned to route better, and a better name should cost a file edit and
/// a restart. The capability is structural: it names the handler that runs, the tier that authorizes
/// it, and the confirmation it stages, so it is a thing only code can mean. Holding both in one
/// string is what made renaming <c>events</c> to <c>get_event_history</c> a C# change, a rebuild, a
/// redeploy, and a stale switch in a browser nobody rebuilt.
/// </para>
/// <para>
/// So: <c>tools.json</c> owns every name, and each entry declares the capability it implements. The
/// catalog resolves the two against each other at startup and refuses a set that does not match the
/// handlers — a capability with no entry, an entry naming a capability nothing implements, or two
/// entries claiming one capability. Renaming a tool is now an edit to that file. <b>Adding</b> one is
/// still code, because a file cannot supply behaviour.
/// </para>
/// <para>
/// ⚠ An id here is permanent once shipped. It appears in no prompt and no UI, so there is never a
/// reason to improve one — and changing it silently unbinds the tool whose entry still names the old
/// id, which the catalog then reports as a missing capability at startup.
/// </para>
/// </summary>
/// <param name="Id">The stable identifier, e.g. <c>instance.status</c>.</param>
public readonly record struct Capability(string Id)
{
    public override string ToString() => Id;
}
