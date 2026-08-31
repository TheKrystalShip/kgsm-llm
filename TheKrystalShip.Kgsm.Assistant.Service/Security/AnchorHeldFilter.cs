using TheKrystalShip.KGSM.Auth.Cluster;

namespace TheKrystalShip.Kgsm.Assistant.Service.Security;

/// <summary>
/// Closes a door that belongs to whichever member holds the cluster's accounts.
/// </summary>
/// <remarks>
/// <para>
/// <b>A door somebody signs in through.</b> A session minted here is scoped to this member and is
/// refused by every other, so a person who came through it would be signed in to one machine and a
/// stranger on the rest — which is the state one sign-in for a cluster exists to end. A second front
/// door also means a second place a credential is presented, on a machine that does not hold the
/// accounts it would be checked against.
/// </para>
/// <para>
/// <b>Nothing else closes.</b> Reads are untouched, every conversation and turn is untouched, and so
/// is signing out: ending a session takes authority away rather than granting it, and this member
/// holds the rows for the sessions it minted. Closing that one would leave somebody unable to end a
/// session that only this machine can end.
/// </para>
/// <para>
/// <b>A machine that is not in a cluster is untouched in every particular.</b> The gate reads cluster
/// state, so a standalone install — and a clustered one whose cluster has no anchor — finds no holder
/// and every door answers exactly as it always has. That is the deployment this service is most often
/// in, and it is not a special case here: it is the absence of one.
/// </para>
/// </remarks>
internal sealed class AnchorHeldFilter(AnchorHeldGate gate) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (await gate.HolderAsync(context.HttpContext.RequestAborted) is not { } elsewhere)
            return await next(context);

        // The holder's NAME, and never its address. A member of a cluster does not tell a caller where
        // that cluster's accounts are: a browser reaches the anchor because somebody gave it the
        // anchor's address, not because a member it happened to find offered one. A name is not an
        // address — it says this door is not the one, without being a way to discover the one.
        context.HttpContext.Response.Headers[AnchorHeld.HolderHeader] = elsewhere.MemberId;

        // 503 rather than 404 or 403: the door exists and this is not a refusal of the caller. It is
        // this member saying it is not the one that answers, which is a different fact from either.
        return Results.Json(
            new { error = AnchorHeld.Code, message = AnchorHeld.Message(elsewhere.MemberId) },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
