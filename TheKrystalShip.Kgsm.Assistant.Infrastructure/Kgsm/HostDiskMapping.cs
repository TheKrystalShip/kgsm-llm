using TheKrystalShip.Kgsm.Assistant.Ports;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Kgsm;

/// <summary>
/// The single mapping from KGSM's reported disk figures onto the assistant's <see cref="HostDisk"/>.
/// Both the health check and the <c>host_info</c> read go through here, so the two cannot describe
/// the same disk differently.
/// </summary>
internal static class HostDiskMapping
{
    /// <summary>
    /// Maps KGSM's disk reading. Returns null for a missing reading — the caller then reports the
    /// disk as unknown rather than assuming a value.
    /// </summary>
    internal static HostDisk? From(DiskInfo? disk) =>
        disk is null
            ? null
            : new HostDisk(
                ParsePercent(disk.UsePercent),
                NullIfEmpty(disk.Size),
                NullIfEmpty(disk.Available),
                NullIfEmpty(disk.Filesystem),
                NullIfEmpty(disk.Used),
                NullIfEmpty(disk.Mount));

    /// <summary>
    /// Reads the leading integer out of a <c>df</c>-style percentage (e.g. <c>"26%"</c>). Null when the
    /// value is absent or unparseable — a threshold check then skips rather than judging an invented
    /// number.
    /// </summary>
    internal static int? ParsePercent(string? usePercent)
    {
        if (string.IsNullOrWhiteSpace(usePercent))
            return null;
        var digits = new string(usePercent.TrimStart().TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var pct) ? pct : null;
    }

    internal static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
