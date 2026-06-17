namespace TheKrystalShip.Kgsm.Assistant.Cli;

/// <summary>Minimal ANSI coloring — no dependency, applied only when color is enabled (TTY + not opted out).</summary>
internal static class Ansi
{
    private const string Reset = "\x1b[0m";
    public const string Dim = "\x1b[2m";
    public const string Red = "\x1b[31m";
    public const string Green = "\x1b[32m";
    public const string Yellow = "\x1b[33m";
    public const string Cyan = "\x1b[36m";
    public const string Bold = "\x1b[1m";

    /// <summary>Wraps <paramref name="text"/> in <paramref name="code"/> when <paramref name="enabled"/>; otherwise returns it plain.</summary>
    public static string Paint(string text, string code, bool enabled) => enabled ? $"{code}{text}{Reset}" : text;
}
