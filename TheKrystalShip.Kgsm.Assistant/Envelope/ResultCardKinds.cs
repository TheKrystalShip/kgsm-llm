using TheKrystalShip.Llm.Models;

namespace TheKrystalShip.Kgsm.Assistant.Envelope;

/// <summary>
/// The <c>tool</c> value a <see cref="ToolResult{TData}"/> carries to a surface — the key the Control
/// Panel's chat switches on to pick which rich card to render.
/// <para>
/// It is deliberately NOT the model-facing tool name. Those two started out as one string and the
/// coupling was silent damage: a name chosen to route a model well is read by a browser that has
/// already shipped, so renaming a tool stops a card rendering in a build nobody rebuilt. The names the
/// model reads are free to change with what routes best; these are a wire contract and change only
/// with the surface that consumes them.
/// </para>
/// <para>
/// ⚠ Changing a value here is a breaking change to <c>kgsm-web</c>'s <c>adaptResultCard</c>. A key it
/// does not know falls through to no card at all — the answer still renders as text, so nothing fails
/// loudly and the loss is invisible until somebody notices the card is gone.
/// </para>
/// </summary>
public static class ResultCardKinds
{
    public static readonly Tool Status = new("get_status");
    public static readonly Tool AuditLog = new("get_audit_log");
    public static readonly Tool Performance = new("get_performance");
    public static readonly Tool Network = new("get_network");
    public static readonly Tool Health = new("run_health_check");
    public static readonly Tool Search = new("search");
    public static readonly Tool RootCause = new("trace_root_cause");
    public static readonly Tool Settling = new("server_command");
    public static readonly Tool BlueprintDraft = new("create_blueprint");
    public static readonly Tool WebPage = new("fetch_url");
}
