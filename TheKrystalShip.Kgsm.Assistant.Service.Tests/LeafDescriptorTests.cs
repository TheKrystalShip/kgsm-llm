using System.Globalization;
using System.Reflection;
using System.Text.Json;
using TheKrystalShip.Kgsm.Assistant;
using TheKrystalShip.Kgsm.Assistant.Infrastructure.Configuration;
using TheKrystalShip.Kgsm.Assistant.Service.Configuration;
using TheKrystalShip.Llm.Agent;
using TheKrystalShip.Llm.Conversation;
using TheKrystalShip.Llm.Ollama;
using TheKrystalShip.Rag.Ollama;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

/// <summary>
/// The leaf config descriptor (<c>deploy/kgsm-llm.leaf.json</c>) is what the Control Panel renders this
/// service's configuration page from. These tests are the anti-drift guard: a setting added to the service
/// without a descriptor entry fails the build here, and a descriptor entry naming a setting the service does
/// not bind fails here too — an override written for one would be reported as applied while changing nothing.
///
/// The coverage check walks the <em>options types the service actually binds</em>, rather than a hand-kept
/// list of key names. That is the equivalent here of the source scan the daemons use: a property added to a
/// bound options class is config the moment it exists, whether or not anyone remembered to write it down.
/// The contract is documented in tks/leaf-config-descriptor.md.
/// </summary>
public class LeafDescriptorTests
{
    /// <summary>
    /// Every configuration section this service binds, with the type it binds into. Adding a
    /// <c>services.Configure&lt;T&gt;(config.GetSection(...))</c> means adding it here — otherwise its
    /// settings are invisible to the coverage check and can drift out of the descriptor unnoticed.
    /// Two types share the "Rag" section on purpose (the retrieval half and the embedding half), and the
    /// flattened key space keeps them apart because their property names do not collide.
    /// </summary>
    private static readonly (string Section, Type Type)[] BoundSections =
    [
        (AssistantServiceOptions.Section, typeof(AssistantServiceOptions)),
        (DiscordOAuthOptions.Section, typeof(DiscordOAuthOptions)),
        (AuthOptions.Section, typeof(AuthOptions)),
        (KgsmConnectionOptions.Section, typeof(KgsmConnectionOptions)),
        (MonitorOptions.Section, typeof(MonitorOptions)),
        (FirewallOptions.Section, typeof(FirewallOptions)),
        (WatchdogOptions.Section, typeof(WatchdogOptions)),
        (InventoryCacheOptions.Section, typeof(InventoryCacheOptions)),
        (WebSearchOptions.Section, typeof(WebSearchOptions)),
        (WebFetchOptions.Section, typeof(WebFetchOptions)),
        (BlueprintAuthoringOptions.Section, typeof(BlueprintAuthoringOptions)),
        (RagOptions.Section, typeof(RagOptions)),
        (RagEmbeddingOptions.Section, typeof(RagEmbeddingOptions)),
        (SearchOptions.Section, typeof(SearchOptions)),
        (LlmAgentOptions.Section, typeof(LlmAgentOptions)),
        (OllamaOptions.Section, typeof(OllamaOptions)),
        (ConversationOptions.Section, typeof(ConversationOptions)),
    ];

    /// <summary>
    /// Real settings that reach the service from outside its own options classes: the ecosystem logging
    /// level, and the listen address the unit sets. Named one by one so the exception cannot quietly widen
    /// into "anything unrecognised is fine".
    /// </summary>
    private static readonly HashSet<string> FrameworkKeys = new(StringComparer.Ordinal)
    {
        "Logging__LogLevel__Default",
        "ASPNETCORE_URLS",
    };

    /// <summary>
    /// Properties on a bound options class that are <strong>not</strong> configuration, each for a stated
    /// reason. Describing one would put a control on the panel that changes nothing.
    /// </summary>
    private static readonly HashSet<string> NotConfiguration = new(StringComparer.Ordinal)
    {
        // Computed at startup from whether the knowledge base is on and whether a web provider is
        // configured, then written back over whatever was bound. Setting either by hand would be
        // overwritten before the first request. (The other computed flags live on types nothing binds
        // from config at all, so they never reach this walk.)
        "Rag__LocalEnabled",
        "Rag__WebEnabled",
    };

    /// <summary>
    /// Settings that are genuinely configurable but <strong>cannot</strong> be delivered through this
    /// channel: a list binds from indexed keys (<c>Rag__Sources__0</c>, <c>__1</c>, …), and one environment
    /// variable cannot express a list of unknown length. Declaring them would promise an edit the panel
    /// could not honour, so they are named here instead — visible, and pinned so the set cannot grow
    /// silently. Editing them stays a file edit until the override channel can carry a list.
    /// </summary>
    private static readonly HashSet<string> NotDeliverableAsOneVariable = new(StringComparer.Ordinal)
    {
        "Auth__AllowedOrigins",
        "WebFetch__AllowedHosts",
        "WebFetch__DeniedHosts",
        "Rag__Sources",
    };

    private static readonly string[] FieldTypes =
        ["string", "int", "bool", "enum", "secret", "path", "csv", "duration", "float"];

    private static readonly string[] RiskLevels = ["safe", "wiring", "destructive"];

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TheKrystalShip.Llm.slnx")))
            dir = dir.Parent;

        Assert.True(dir is not null, "could not locate the repo root (no TheKrystalShip.Llm.slnx above the test binary)");
        return dir!.FullName;
    }

    private static JsonElement Descriptor()
    {
        string path = Path.Combine(RepoRoot(), "deploy", "kgsm-llm.leaf.json");
        Assert.True(File.Exists(path), $"the leaf descriptor is missing: {path}");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    }

    private static List<JsonElement> Fields() => [.. Descriptor().GetProperty("fields").EnumerateArray()];

    private static string Str(JsonElement field, string name) => field.GetProperty(name).GetString()!;

    private static string? OptionalStr(JsonElement field, string name) =>
        field.TryGetProperty(name, out JsonElement v) ? v.GetString() : null;

    /// <summary>
    /// Every setting the service binds, flattened the way IConfiguration maps an environment variable
    /// (<c>Section__Key</c>, nested one <c>__</c> deeper). Lists are excluded: they bind from indexed keys,
    /// which a single variable cannot express.
    /// </summary>
    private static HashSet<string> SettingsInBoundOptions()
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string section, Type type) in BoundSections)
            Walk(type, section, found, depth: 0);

        found.ExceptWith(NotConfiguration);
        found.ExceptWith(NotDeliverableAsOneVariable);
        Assert.NotEmpty(found);   // a walk that finds nothing would pass every check below vacuously
        return found;
    }

    private static void Walk(Type type, string prefix, HashSet<string> into, int depth)
    {
        Assert.True(depth < 4, $"{prefix}: options nesting is deeper than this walk expects");

        foreach (PropertyInfo p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetSetMethod() is null)
                continue;   // read-only: computed, not bound

            string key = $"{prefix}__{p.Name}";
            Type t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;

            if (t == typeof(string) || t.IsPrimitive || t.IsEnum || t == typeof(decimal))
                into.Add(key);
            else if (t.IsArray || (t.IsGenericType && typeof(System.Collections.IEnumerable).IsAssignableFrom(t)))
                into.Add(key);   // a list — kept so the exclusion list has to name it explicitly
            else
                Walk(t, key, into, depth + 1);   // a nested options object
        }
    }

    // ── Coverage: the descriptor and the bound options agree, both ways ──────

    [Fact]
    public void Every_setting_the_service_binds_is_described()
    {
        var described = Fields().Select(f => Str(f, "env")).ToHashSet(StringComparer.Ordinal);
        var missing = SettingsInBoundOptions().Where(k => !described.Contains(k))
            .OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0,
            "these settings are bound by the service but not described in deploy/kgsm-llm.leaf.json, so the " +
            "Control Panel cannot show or set them:\n  " + string.Join("\n  ", missing));
    }

    [Fact]
    public void Every_described_setting_is_real()
    {
        var bound = SettingsInBoundOptions();
        var fabricated = Fields()
            .Select(f => Str(f, "env"))
            .Where(e => !bound.Contains(e) && !FrameworkKeys.Contains(e))
            .OrderBy(e => e, StringComparer.Ordinal)
            .ToList();

        Assert.True(fabricated.Count == 0,
            "these descriptor fields name settings the service does not bind — an override written for one " +
            "would be reported as applied while changing nothing:\n  " + string.Join("\n  ", fabricated));
    }

    /// <summary>
    /// The list-valued settings stay excluded deliberately, and stay excluded for the stated reason. If one
    /// ever stops being a list, this fails and it has to be described rather than silently staying hidden.
    /// </summary>
    [Fact]
    public void Excluded_list_settings_are_still_lists()
    {
        foreach (string key in NotDeliverableAsOneVariable)
        {
            string[] parts = key.Split("__");
            (string Section, Type Type) bound = BoundSections.First(b => b.Section == parts[0]
                && b.Type.GetProperty(parts[1]) is not null);
            Type t = bound.Type.GetProperty(parts[1])!.PropertyType;

            Assert.True(t.IsArray || (t.IsGenericType && typeof(System.Collections.IEnumerable).IsAssignableFrom(t)),
                $"{key} is no longer a list, so it can be delivered as one variable and belongs in the descriptor");
        }
    }

    // ── Structure ────────────────────────────────────────────────────────────

    [Fact]
    public void Descriptor_identity_matches_this_project()
    {
        JsonElement d = Descriptor();

        Assert.Equal(1, d.GetProperty("schemaVersion").GetInt32());
        // The leaf id is "assistant", not the repo name — kgsm-api knows this service by what it does.
        Assert.Equal("assistant", d.GetProperty("id").GetString());
        Assert.Equal("kgsm-assistant-service.service", d.GetProperty("unit").GetString());
        Assert.Equal("restart", d.GetProperty("applyMode").GetString());
        Assert.False(d.GetProperty("onDemand").GetBoolean());
        Assert.NotEmpty(d.GetProperty("displayName").GetString()!);
        Assert.NotEmpty(d.GetProperty("role").GetString()!);
    }

    [Fact]
    public void Floor_sources_are_declared_and_typed()
    {
        var kinds = new[] { "systemd-unit", "env-file", "appsettings" };

        var sources = Descriptor().GetProperty("floorSources").EnumerateArray().ToList();
        Assert.NotEmpty(sources);

        foreach (JsonElement s in sources)
        {
            Assert.Contains(Str(s, "kind"), kinds);
            Assert.StartsWith("/", Str(s, "path"));
        }
    }

    [Fact]
    public void Field_keys_are_unique()
    {
        var keys = Fields().Select(f => Str(f, "key")).ToList();
        var dupes = keys.GroupBy(k => k, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        Assert.True(dupes.Count == 0, "duplicate field keys: " + string.Join(", ", dupes));
        Assert.Equal(keys.Count, Fields().Select(f => Str(f, "env")).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Every_field_is_completely_described()
    {
        foreach (JsonElement f in Fields())
        {
            string key = Str(f, "key");

            Assert.False(string.IsNullOrWhiteSpace(key), "a field has no key");
            Assert.False(string.IsNullOrWhiteSpace(OptionalStr(f, "label")), $"{key}: no label");
            Assert.False(string.IsNullOrWhiteSpace(OptionalStr(f, "description")), $"{key}: no description");

            string type = Str(f, "type");
            Assert.True(FieldTypes.Contains(type), $"{key}: unknown type '{type}'");

            string risk = OptionalStr(f, "risk") ?? "safe";
            Assert.True(RiskLevels.Contains(risk), $"{key}: unknown risk '{risk}'");

            if (f.TryGetProperty("default", out JsonElement def))
                Assert.Equal(JsonValueKind.String, def.ValueKind);
        }
    }

    /// <summary>A secret is never echoed back, so a default for one would be a credential written into a
    /// file that ships in the repo.</summary>
    [Fact]
    public void Secrets_carry_no_default()
    {
        foreach (JsonElement f in Fields().Where(f => Str(f, "type") == "secret"))
            Assert.False(f.TryGetProperty("default", out _), $"{Str(f, "key")}: a secret must not carry a default");
    }

    [Fact]
    public void Enum_fields_carry_their_values_and_a_valid_default()
    {
        foreach (JsonElement f in Fields().Where(f => Str(f, "type") == "enum"))
        {
            string key = Str(f, "key");
            var values = f.GetProperty("values").EnumerateArray().Select(v => v.GetString()).ToList();
            Assert.NotEmpty(values);

            string? def = OptionalStr(f, "default");
            if (def is not null)
                Assert.True(values.Contains(def), $"{key}: default '{def}' is not one of its values");
        }
    }

    [Fact]
    public void Numeric_bounds_and_units_are_coherent()
    {
        foreach (JsonElement f in Fields())
        {
            string key = Str(f, "key");
            bool numeric = Str(f, "type") is "int" or "duration" or "float";

            if (!numeric)
            {
                Assert.False(f.TryGetProperty("min", out _), $"{key}: min on a non-numeric field");
                Assert.False(f.TryGetProperty("max", out _), $"{key}: max on a non-numeric field");
                continue;
            }

            if (OptionalStr(f, "default") is { } def)
            {
                Assert.True(double.TryParse(def, NumberStyles.Float, CultureInfo.InvariantCulture, out double value),
                    $"{key}: default '{def}' is not a number");
                if (f.TryGetProperty("min", out JsonElement min))
                    Assert.True(value >= min.GetDouble(), $"{key}: default {value} is below its own min");
                if (f.TryGetProperty("max", out JsonElement max))
                    Assert.True(value <= max.GetDouble(), $"{key}: default {value} is above its own max");
            }
        }
    }

    [Fact]
    public void Bool_defaults_are_the_wire_representation()
    {
        foreach (JsonElement f in Fields().Where(f => Str(f, "type") == "bool"))
        {
            string? def = OptionalStr(f, "default");
            if (def is not null)
                Assert.True(def is "true" or "false", $"{Str(f, "key")}: bool default must be 'true' or 'false', got '{def}'");
        }
    }

    [Fact]
    public void Group_and_dependency_references_resolve()
    {
        JsonElement d = Descriptor();

        var groups = d.TryGetProperty("groups", out JsonElement g)
            ? g.EnumerateArray().Select(x => x.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal)
            : [];
        var keys = Fields().Select(f => Str(f, "key")).ToHashSet(StringComparer.Ordinal);

        foreach (JsonElement f in Fields())
        {
            string key = Str(f, "key");

            if (OptionalStr(f, "group") is { } group)
                Assert.True(groups.Contains(group), $"{key}: references group '{group}', which is not defined");

            if (OptionalStr(f, "dependsOn") is { } dep)
            {
                Assert.True(keys.Contains(dep), $"{key}: dependsOn '{dep}', which is not a field here");
                Assert.NotEqual(key, dep);
            }
        }
    }

    [Fact]
    public void Wire_keys_already_in_use_by_the_api_are_preserved()
    {
        // Overrides stored by kgsm-api are keyed by these. Renaming one orphans a live override and
        // silently reverts the assistant to its floor, so they are pinned here deliberately.
        var keys = Fields().Select(f => Str(f, "key")).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("logLevel", keys);
        Assert.Contains("ragEnabled", keys);
        Assert.Contains("webSearchApiKey", keys);
    }
}
