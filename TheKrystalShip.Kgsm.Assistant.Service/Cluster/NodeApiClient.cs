using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

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
    public async Task<NodeResult<T>> GetAsync<T>(
        ClusterNode node, string path, JsonTypeInfo<T> shape, CancellationToken ct)
    {
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                using HttpRequestMessage request = Build(HttpMethod.Get, node, path);
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

                if (!response.IsSuccessStatusCode)
                {
                    return new NodeResult<T>(
                        node.MemberId, default, $"answered HTTP {(int)response.StatusCode}");
                }

                await using Stream body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                return new NodeResult<T>(
                    node.MemberId, await JsonSerializer.DeserializeAsync(body, shape, ct).ConfigureAwait(false), null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (attempt == 1)
                    continue;

                logger.LogWarning(ex, "could not reach node {Node} at {Url}", node.MemberId, node.Url);
                return new NodeResult<T>(node.MemberId, default, "could not be reached");
            }
        }

        return new NodeResult<T>(node.MemberId, default, "could not be reached");
    }

    private HttpRequestMessage Build(HttpMethod method, ClusterNode node, string path)
    {
        var request = new HttpRequestMessage(method, $"{node.Url}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.Mint().Token);

        // Absent when nobody is behind the turn — a sweep, a warm-up. The node then refuses, which is
        // correct: there is no person to act for, and acting as the member itself would be acting as
        // nobody with a machine's credential.
        if (invocation.Current?.Handle is { Length: > 0 } handle)
            request.Headers.TryAddWithoutValidation(MemberActing.ActingHandleHeader, handle);

        return request;
    }
}
