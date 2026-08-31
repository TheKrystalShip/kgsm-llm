using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using TheKrystalShip.Api.Contracts;

using TheKrystalShip.Kgsm.Assistant.Infrastructure;
using TheKrystalShip.KGSM.Auth.Cluster;
using TheKrystalShip.KGSM.Cluster.Identity;

namespace TheKrystalShip.Kgsm.Assistant.Service.Cluster;

/// <summary>What a node answered, or why it did not.</summary>
/// <param name="Node">The node this is about, always — a failure that does not name its node is a
/// fleet answer with a hole in it that nobody can see.</param>
/// <param name="Body">The parsed response, or <see langword="null"/> when there is none.</param>
/// <param name="Failure">Why it could not be answered, or <see langword="null"/> when it was.</param>
public sealed record NodeResult<T>(string Node, T? Body, string? Failure)
{
    public bool Answered => Failure is null;
}

/// <summary>
/// Calls a node's Control Panel API as a member of the cluster, acting for the person whose turn this
/// is.
/// </summary>
/// <remarks>
/// <para>
/// <b>The caller is this member; the person is named.</b> No session belonging to anybody crosses the
/// wire — the assistant has one only for a browser caller and none at all for somebody in Discord, and
/// forwarding one would put a credential on a second machine and make a long action fail the moment it
/// expired. The node resolves what the named person may do from its own replica of the cluster's
/// accounts, so nothing here can grant anything.
/// </para>
/// <para>
/// <b>Who is asked for is the turn's, not the caller's choice.</b> The handle comes from the ambient
/// invocation, which the host sets once from the authenticated principal — the same place the audit
/// actor comes from — so a tool cannot name somebody else by passing a different argument.
/// </para>
/// <para>
/// <b>One retry, then an honest failure.</b> A node that is restarting or briefly unreachable is the
/// ordinary case on a cluster whose links flap; a node that is down stays down, and pretending
/// otherwise turns one unreachable machine into a fleet-wide error.
/// </para>
/// </remarks>
public sealed class NodeApiClient(
    IHttpClientFactory http,
    IClusterTokenService tokens,
    IInvocationContext invocation,
    ILogger<NodeApiClient> logger)
{
    /// <summary>The named client node calls are made on — its own timeout, separate from the model's.</summary>
    public const string HttpClientName = "kgsm-node-api";

    /// <summary>
    /// Read something from a node, as the person this turn belongs to.
    /// </summary>
    public Task<NodeResult<T>> GetAsync<T>(
        ClusterNode node, string path, JsonTypeInfo<T> shape, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, node, path, null, shape, retry: true, ct);

    /// <summary>
    /// Ask a node to do something, as the person this turn belongs to, and read what it answered.
    /// </summary>
    /// <remarks>
    /// <b>A write is sent once.</b> The read path retries because asking the same question twice costs
    /// nothing; a request that may already have started a server, deleted a backup or banned somebody
    /// is not the same request twice. A connection that dropped after the node received it looks
    /// exactly like one that dropped before, so a second attempt is a second action taken on a guess.
    /// </remarks>
    public Task<NodeResult<T>> SendAsync<T>(
        HttpMethod method, ClusterNode node, string path, string? json, JsonTypeInfo<T> shape,
        CancellationToken ct) =>
        SendAsync(method, node, path, json, shape, retry: false, ct);

    /// <summary>The same, for a call whose answer is only whether it worked.</summary>
    public async Task<NodeResult<NodeNothing>> SendAsync(
        HttpMethod method, ClusterNode node, string path, string? json, CancellationToken ct)
    {
        NodeResult<ErrorEnvelope> sent =
            await SendAsync(method, node, path, json, ApiContractsJson.Default.ErrorEnvelope,
                retry: false, ct).ConfigureAwait(false);

        return new NodeResult<NodeNothing>(
            sent.Node, sent.Answered ? new NodeNothing() : null, sent.Failure);
    }

    private async Task<NodeResult<T>> SendAsync<T>(
        HttpMethod method, ClusterNode node, string path, string? json, JsonTypeInfo<T> shape,
        bool retry, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using HttpRequestMessage request = Build(method, node, path, json);
                using HttpResponseMessage response =
                    await http.CreateClient(HttpClientName).SendAsync(request, ct).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    // The node was reached and would not let this member act for this person. Which of
                    // the reasons it is — no account there under that handle, a disabled one, a node
                    // too old to know the scheme — is the node's to know and is not on the wire, so
                    // what is reported is the refusal itself. Told apart from unreachable, because
                    // everything else about the node is healthy and saying otherwise would send
                    // somebody looking at a network.
                    return new NodeResult<T>(
                        node.MemberId, default, "would not let this assistant act for you");
                }

                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    // Resolved to a real person and refused on what they hold. Said as a permission,
                    // because reporting it as a failure sends somebody to look at a server that is
                    // working exactly as configured.
                    return new NodeResult<T>(
                        node.MemberId, default, "that account is not allowed to do this here");
                }

                if (!response.IsSuccessStatusCode)
                {
                    // The node's own sentence when it wrote one. A refusal it worded — a game that
                    // declares no ban command, a start on a server already running — is the whole of
                    // the answer, and replacing it with a status code throws away the only part
                    // anybody can act on.
                    string? said = await ReasonAsync(response, ct).ConfigureAwait(false);
                    return new NodeResult<T>(
                        node.MemberId, default, said ?? $"answered HTTP {(int)response.StatusCode}");
                }

                if (response.StatusCode == HttpStatusCode.NoContent
                    || response.Content.Headers.ContentLength is 0)
                {
                    return new NodeResult<T>(node.MemberId, default, null);
                }

                await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                return new NodeResult<T>(
                    node.MemberId,
                    await JsonSerializer.DeserializeAsync(stream, shape, ct).ConfigureAwait(false),
                    null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (retry && attempt == 1)
                    continue;

                logger.LogWarning(ex, "could not reach node {Node} at {Url}", node.MemberId, node.Url);
                return new NodeResult<T>(node.MemberId, default, "could not be reached");
            }
        }
    }

    /// <summary>What the node said went wrong, in its own words, or null when it wrote nothing.</summary>
    private static async Task<string?> ReasonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            ErrorEnvelope? refusal = await JsonSerializer
                .DeserializeAsync(stream, ApiContractsJson.Default.ErrorEnvelope, ct).ConfigureAwait(false);

            return string.IsNullOrWhiteSpace(refusal?.Error?.Message) ? null : refusal!.Error!.Message;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private HttpRequestMessage Build(HttpMethod method, ClusterNode node, string path, string? json)
    {
        var request = new HttpRequestMessage(method, $"{node.Url}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.Mint().Token);

        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        // Absent when nobody is behind the turn — a sweep, a warm-up. The node then refuses, which is
        // correct: there is no person to act for, and acting as the member itself would be acting as
        // nobody with a machine's credential.
        if (invocation.Current?.Handle is { Length: > 0 } handle)
            request.Headers.TryAddWithoutValidation(MemberActing.ActingHandleHeader, handle);

        return request;
    }
}
