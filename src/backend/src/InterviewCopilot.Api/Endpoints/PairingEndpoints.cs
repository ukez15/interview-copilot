using System.Security.Claims;
using InterviewCopilot.Api.Infrastructure;
using InterviewCopilot.Application.Pairing;

namespace InterviewCopilot.Api.Endpoints;

public sealed record ClaimPairingRequest(string Code, string Name);

public sealed record ClaimPairingResponse(string Token);

internal static class PairingEndpoints
{
    /// <summary>Rate limiter policy for the anonymous claim endpoint: 5 requests a minute per remote IP.</summary>
    public const string ClaimRateLimitPolicy = "pairing-claim";

    public const int ClaimsPerMinute = 5;

    public static void MapPairingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/pairing").WithTags("Pairing");

        group.MapPost("/claim", async (ClaimPairingRequest request, PairingService pairing, CancellationToken ct) =>
                new ClaimPairingResponse(await pairing.ClaimAsync(request.Code, request.Name, ct)))
            .AllowAnonymous()
            .RequireRateLimiting(ClaimRateLimitPolicy);

        // Bearer only: a paired browser can't mint codes or list/revoke browsers.
        var owner = group.MapGroup(string.Empty).RequireAuthorization();

        owner.MapPost("/codes", (ClaimsPrincipal user, PairingService pairing) => pairing.IssueCode(user.UserId()));

        owner.MapGet("/browsers", (ClaimsPrincipal user, PairingService pairing, CancellationToken ct) =>
            pairing.ListAsync(user.UserId(), ct));

        owner.MapDelete("/browsers/{browserId:guid}", async (
            Guid browserId,
            ClaimsPrincipal user,
            PairingService pairing,
            CancellationToken ct) =>
        {
            await pairing.RevokeAsync(user.UserId(), browserId, ct);
            return Results.NoContent();
        });
    }
}
