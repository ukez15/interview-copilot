using System.Security.Claims;
using InterviewCopilot.Application.Auth;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace InterviewCopilot.Api.Endpoints;

public sealed record CredentialsRequest(string Username, string Password);

public sealed record RefreshRequest(string? RefreshToken);

internal static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/register", async (CredentialsRequest request, AccountService accounts, CancellationToken ct) =>
        {
            var account = await accounts.RegisterAsync(request.Username, request.Password, ct);
            return TypedResults.Created($"/api/auth/accounts/{account.Id}", account);
        });

        group.MapPost("/sign-in", async Task<Results<SignInHttpResult, UnauthorizedHttpResult>> (
            CredentialsRequest request,
            AccountService accounts,
            CancellationToken ct) =>
        {
            var account = await accounts.SignInAsync(request.Username, request.Password, ct);
            if (account is null)
            {
                return TypedResults.Unauthorized();
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, account.Id.ToString()),
                    new Claim(ClaimTypes.Name, account.Username),
                ],
                BearerTokenDefaults.AuthenticationScheme);
            return TypedResults.SignIn(new ClaimsPrincipal(identity), authenticationScheme: BearerTokenDefaults.AuthenticationScheme);
        });

        // Stay signed in across restarts (#8): exchange a still-valid refresh token for new tokens.
        group.MapPost("/refresh", Results<SignInHttpResult, UnauthorizedHttpResult> (
            RefreshRequest request,
            IOptionsMonitor<BearerTokenOptions> bearerOptions,
            TimeProvider clock) =>
        {
            if (string.IsNullOrEmpty(request.RefreshToken))
            {
                return TypedResults.Unauthorized();
            }

            var protector = bearerOptions.Get(BearerTokenDefaults.AuthenticationScheme).RefreshTokenProtector;
            var ticket = protector.Unprotect(request.RefreshToken);
            if (ticket?.Properties.ExpiresUtc is not { } expires || expires < clock.GetUtcNow())
            {
                return TypedResults.Unauthorized();
            }

            return TypedResults.SignIn(ticket.Principal, authenticationScheme: BearerTokenDefaults.AuthenticationScheme);
        });
    }
}
