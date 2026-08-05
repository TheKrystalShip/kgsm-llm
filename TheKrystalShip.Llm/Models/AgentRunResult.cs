namespace TheKrystalShip.Llm.Models;

/// <summary>
/// The outcome of a buffered agent turn: the final reply <see cref="Text"/> plus the token
/// <see cref="Usage"/> of the call that produced it (null if the backend reported none, e.g. the
/// iteration-cap reply). The streaming path carries the same usage on its terminal
/// <c>AgentEvent.Final</c>, so both paths expose context occupancy identically.
/// </summary>
public sealed record AgentRunResult(string Text, LlmUsage? Usage, long TurnId = 0);
