using System.Net;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using TheKrystalShip.Kgsm.Assistant.Infrastructure;
using TheKrystalShip.Kgsm.Assistant.Service.Cluster;
using TheKrystalShip.KGSM.Cluster;
using TheKrystalShip.KGSM.Cluster.Identity;
using TheKrystalShip.KGSM.Cluster.Membership;
using TheKrystalShip.KGSM.Cluster.Storage;

namespace TheKrystalShip.Kgsm.Assistant.Service.Tests;

/// <summary>
/// A node that answers what a real one would, over the real client.
/// </summary>
/// <remarks>
/// <para>
/// The canned bodies are kgsm-api's own JSON, deserialized by the contract package's own context, so a
/// test that passes has exercised the wire names as well as the mapping. Handing the adapter a
/// hand-built DTO instead would pass while every property name on the wire was wrong, which is the one
/// failure the shared package exists to prevent.
/// </para>
/// <para>
/// The roster is a real store on a temp file rather than a stub, because <c>NodeDirectory</c> reads one
/// and its rules — anchors skipped, disabled members skipped, a member with no address skipped — are
/// part of what a routed read depends on.
/// </para>
/// </remarks>
internal sealed class StubNode : IDisposable
{
    /// <summary>The single node's id, when a test does not care which.</summary>
    public const string Member = "n1";

    private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _answers = new(StringComparer.Ordinal);
    private readonly string _storePath = Path.Combine(
        Path.GetTempPath(), $"kgsm-stub-node-{Guid.NewGuid():N}.db");

    private readonly ClusterStore _store;

    public StubNode(params string[]? members)
    {
        string[] ids = members is { Length: > 0 } ? members : [Member];

        var options = new ClusterOptions
        {
            MemberId = "assistant",
            StorePath = _storePath,
            Secret = "a-cluster-secret",
        };

        _store = new ClusterStore(options, NullLogger<ClusterStore>.Instance);
        _store.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

        var roster = new MembersStore(_store);
        foreach (string id in ids)
        {
            roster.UpsertAsync(
                MemberRow.New(id, MemberKind.Node) with { Url = $"https://{id}.test" },
                CancellationToken.None).GetAwaiter().GetResult();
        }

        Directory = new NodeDirectory(roster, options);

        var tokens = Substitute.For<IClusterTokenService>();
        tokens.Mint().Returns(new MintedClusterToken("stub-token", DateTimeOffset.UtcNow.AddMinutes(5)));

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(NodeApiClient.HttpClientName)
            .Returns(_ => new HttpClient(new Handler(_answers)));

        // A person behind the turn, because a node refuses a read that names nobody — and every read
        // under test is one somebody asked for.
        var invocation = new AsyncLocalInvocationContext();
        invocation.Begin(Invocation.ForAssistant("tester", null, "local:usr_test"));

        Client = new NodeApiClient(factory, tokens, invocation, NullLogger<NodeApiClient>.Instance);
    }

    public NodeDirectory Directory { get; }

    public NodeApiClient Client { get; }

    /// <summary>What the node answers at this path, as kgsm-api would write it.</summary>
    public void Answer(string path, string json) => _answers[path] = (HttpStatusCode.OK, json);

    /// <summary>What the node refuses at this path, in the API's frozen error envelope.</summary>
    public void Refuse(string path, HttpStatusCode status) =>
        _answers[path] = (status, """{"error":{"code":"unavailable","message":"the node said no"}}""");

    public ClusterServers Servers() => new(
        Directory, Client, Catalog(), Invocations(), Settings, NullLogger<ClusterServers>.Instance);

    public ClusterCatalog Catalog() => new(
        Directory, Client, Invocations(), Settings, NullLogger<ClusterCatalog>.Instance);

    public ClusterServerFacts Facts() => new(Servers(), Directory, Client);

    public ClusterServerMetrics Metrics() => new(Servers(), Client);

    public ClusterNetworkInfo Network() => new(Servers(), Client);

    /// <summary>No window: each read in a test asks the node again, so one case cannot answer another.</summary>
    private static AssistantClusterSettings Settings { get; } =
        new("assistant", string.Empty, TimeSpan.Zero);

    private static IInvocationContext Invocations()
    {
        var invocation = new AsyncLocalInvocationContext();
        invocation.Begin(Invocation.ForAssistant("tester", null, "local:usr_test"));
        return invocation;
    }

    public void Dispose()
    {
        _store.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        try { File.Delete(_storePath); } catch (IOException) { }
    }

    /// <summary>Answers the canned body for a path, and 404s anything a test did not set up — so a
    /// read of a route nobody stubbed fails loudly rather than passing on a default.</summary>
    private sealed class Handler(Dictionary<string, (HttpStatusCode Status, string Body)> answers)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;

            if (!answers.TryGetValue(path, out (HttpStatusCode Status, string Body) answer))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent(
                        $$$"""{"error":{"code":"not_found","message":"no stub for {{{path}}}"}}""",
                        Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(answer.Status)
            {
                Content = new StringContent(answer.Body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
