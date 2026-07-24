using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.Kgsm.Assistant.Infrastructure.Configuration;
using TheKrystalShip.Kgsm.Assistant.Ports;

namespace TheKrystalShip.Kgsm.Assistant.Infrastructure.Blueprints;

/// <summary>
/// The real <see cref="IBlueprintAttemptStore"/>: one directory per attempt under
/// <see cref="BlueprintAuthoringOptions.StashDir"/>, holding the draft YAML (if the pipeline got that
/// far), the per-field provenance, and the verify trace — plain files, reviewable by an admin without
/// any dedicated UI (v1; a kgsm-web admin surface is a future addition, not built here). Never throws:
/// a write failure is logged and swallowed, matching the port's contract (a stash failure must never
/// turn an honest "couldn't do this one" into an error for the end user).
/// </summary>
internal sealed class FileSystemBlueprintAttemptStore : IBlueprintAttemptStore
{
    private readonly BlueprintAuthoringOptions _options;
    private readonly ILogger<FileSystemBlueprintAttemptStore> _logger;

    public FileSystemBlueprintAttemptStore(
        IOptions<BlueprintAuthoringOptions> options, ILogger<FileSystemBlueprintAttemptStore> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task RecordAsync(BlueprintAttemptRecord record, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.StashDir))
        {
            _logger.LogDebug("Blueprint-authoring stash dir is unset — dropping the attempt record for \"{Game}\"", record.Game);
            return;
        }

        try
        {
            var dirName = $"{record.AttemptedAt:yyyyMMdd-HHmmss}-{Slug(record.Game)}";
            var dir = Path.Combine(_options.StashDir, dirName);
            Directory.CreateDirectory(dir);

            var meta = JsonSerializer.Serialize(new
            {
                record.Game,
                record.BlueprintName,
                record.AttemptedAt,
                Outcome = record.Outcome.ToString(),
                record.Reason,
                Provenance = record.Provenance.Select(p => new { p.Field, p.Value, p.SourceUrl }),
            }, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(dir, "attempt.json"), meta, cancellationToken);

            if (record.DraftYaml is not null)
                await File.WriteAllTextAsync(Path.Combine(dir, "draft.yaml"), record.DraftYaml, cancellationToken);

            if (record.VerifyLog.Count > 0)
            {
                var log = new StringBuilder();
                foreach (var line in record.VerifyLog)
                    log.AppendLine(line);
                await File.WriteAllTextAsync(Path.Combine(dir, "verify.log"), log.ToString(), cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write the blueprint-authoring attempt stash for \"{Game}\"", record.Game);
        }
    }

    private static string Slug(string game)
    {
        var chars = game.Trim().ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')
            .ToArray();
        var slug = new string(chars).Trim('-');
        return string.IsNullOrEmpty(slug) ? "unknown" : slug;
    }
}
