using FluentAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.Api.Contracts;
using TheKrystalShip.KGSM.ComponentSurface;

using Xunit;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

/// <summary>
/// What this service answers about <b>itself</b>: its configuration, its unit and its journal.
/// </summary>
/// <remarks>
/// <para>
/// A component owns these wherever it runs — a node's leaf on a machine standing alone, the cluster's
/// anchor otherwise — and only the way a browser reaches them differs. The rules behind them belong to
/// <c>TheKrystalShip.KGSM.ComponentSurface</c> and are tested in the repo that also generates the
/// descriptor they read. What is under test here is this service's half: that the composition resolves
/// the surface at all, and that the paths it resolves are the ones the deploy writes to.
/// </para>
/// <para>
/// Every path is pinned at a temp directory. Left at their defaults they resolve to the live
/// <c>/var/lib/kgsm/anchors/assistant.json</c> and the override file the running service reads, and a
/// test writing there would change what the host is running.
/// </para>
/// </remarks>
public sealed class OwnSurfaceTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;

    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "kgsm-assistant-surface-" + Guid.NewGuid().ToString("N"))).FullName;

    public OwnSurfaceTests(WebApplicationFactory<Program> factory) => _factory = factory;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp dir that outlives a run costs nothing */ }
    }

    private string DescriptorPath => Path.Combine(_dir, "assistant.json");

    private string OverridePath => Path.Combine(_dir, "config-override.env");

    private IServiceProvider Services(bool withDescriptor = true)
    {
        if (withDescriptor)
        {
            // The descriptor this repo's own build generated, which is the file a deploy installs.
            File.Copy(RepoDescriptor(), DescriptorPath, overwrite: true);
        }

        return _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Assistant:Surface:DescriptorPath", DescriptorPath);
            b.UseSetting("Assistant:Surface:OverridePath", OverridePath);
            // The stores, relocated like every path above. At their defaults they are the machine's own
            // — this service's live conversations and the account replica every service reads — which a
            // test must never open, and which a machine that runs no assistant does not have at all.
            b.UseSetting("Auth:UsersDbPath", Path.Combine(_dir, "users.db"));
            b.UseSetting("Conversation:DatabasePath", Path.Combine(_dir, "conversations.db"));
        }).Services;
    }

    // deploy/kgsm-llm.anchor.json, found by walking up from the test binary — the same file the
    // generator rewrites on every build, so this cannot drift from what ships.
    private static string RepoDescriptor()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "deploy", "kgsm-llm.anchor.json");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException("deploy/kgsm-llm.anchor.json was not found above the test binary.");
    }

    [Fact]
    public void The_composition_resolves_this_services_own_surface()
    {
        IServiceProvider sp = Services();

        // Every one of these is resolved by an endpoint on the /admin group, so a registration missing
        // here is a 500 the gate would hide behind a 401 on an unauthenticated probe.
        sp.GetRequiredService<ComponentSurfaceOptions>().Should().NotBeNull();
        sp.GetRequiredService<ComponentConfigService>().Should().NotBeNull();
        sp.GetRequiredService<ComponentUnitReader>().Should().NotBeNull();
        sp.GetRequiredService<ComponentJournal>().Should().NotBeNull();
        sp.GetRequiredService<ComponentJournalFollower>().Should().NotBeNull();
    }

    [Fact]
    public void The_surface_reads_the_descriptor_this_build_generated()
    {
        ComponentConfigView? view = Services().GetRequiredService<ComponentConfigService>().Read();

        view.Should().NotBeNull();
        view!.Id.Should().Be("assistant", "the wire names the component by the id its descriptor declares");
        view.Unit.Should().Be("kgsm-assistant-service.service");
        view.FromDescriptor.Should().BeTrue();
        view.Fields.Should().NotBeEmpty();

        // A knob a reader would look for, proving this is the real surface rather than a stub.
        view.Fields.Should().Contain(f => f.Key == "actionsEnabled");
    }

    [Fact]
    public void A_host_with_no_descriptor_serves_no_surface_rather_than_an_empty_one()
    {
        ComponentConfigService config = Services(withDescriptor: false)
            .GetRequiredService<ComponentConfigService>();

        config.Read().Should().BeNull(
            "no descriptor is a different fact from a surface with no fields, and the endpoint answers 404");
    }

    [Fact]
    public void Every_field_says_which_tier_it_is_running_on()
    {
        ComponentConfigView view = Services().GetRequiredService<ComponentConfigService>().Read()!;

        view.Fields.Should().OnlyContain(
            f => f.Source == ComponentConfigSource.Override
              || f.Source == ComponentConfigSource.Floor
              || f.Source == ComponentConfigSource.Default
              || f.Source == ComponentConfigSource.Unknown,
            "resetting a key restores a different thing in each tier, so the page has to name which");
    }

    [Fact]
    public void A_secret_is_never_echoed_back()
    {
        ComponentConfigView view = Services().GetRequiredService<ComponentConfigService>().Read()!;

        view.Fields.Where(f => f.IsSecret).Should()
            .OnlyContain(f => f.Value == null && f.Effective == null && f.Floor == null,
                "knowing a secret is set is not knowing the secret");
    }

    [Fact]
    public async Task The_unit_row_carries_this_components_identity()
    {
        ComponentService? row = await Services().GetRequiredService<ComponentUnitReader>().ReadAsync();

        row.Should().NotBeNull();
        row!.Id.Should().Be("assistant");
        row.Unit.Should().Be("kgsm-assistant-service.service");
        // The link axis belongs to an API holding a connection to something else. A component serving
        // its own row has none, and null renders as "not applicable" rather than "disconnected".
        row.Provisioned.Should().BeNull();
    }
}
