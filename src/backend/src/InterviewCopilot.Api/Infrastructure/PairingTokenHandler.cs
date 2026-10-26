using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using InterviewCopilot.Application.Pairing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace InterviewCopilot.Api.Infrastructure;

/// <summary>Names used by the pairing-token authentication scheme (#33).</summary>
internal static class PairingTokenDefaults
{
    public const string Scheme = "PairingToken";

    public const string Header = "X-Pairing-Token";

    public const string QueryKey = "pairing_token";

    public const string ScopeClaim = "scope";

    public const string ExtensionScope = "extension";

    public const string BrowserIdClaim = "browser_id";
}

/// <summary>
/// Authenticates a paired browser extension by its token (header, or query string on the events socket only).
/// The principal carries the owner's user id plus <c>scope=extension</c>. Tokens and hashes are never logged.
/// </summary>
internal sealed partial class PairingTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    PairingService pairing) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = ReadToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            return AuthenticateResult.NoResult();
        }

        var identity = await pairing.AuthenticateAsync(token, Context.RequestAborted);
        if (identity is null)
        {
            return AuthenticateResult.Fail("Unknown or revoked pairing token.");
        }

        var claims = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, identity.UserId.ToString()),
                new Claim(PairingTokenDefaults.ScopeClaim, PairingTokenDefaults.ExtensionScope),
                new Claim(PairingTokenDefaults.BrowserIdClaim, identity.BrowserId.ToString()),
            ],
            Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(claims), Scheme.Name));
    }

    [GeneratedRegex(@"^/ws/sessions/[^/]+/events/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex EventsPath();

    private string? ReadToken()
    {
        if (Request.Headers.TryGetValue(PairingTokenDefaults.Header, out var header) && header.Count > 0)
        {
            return header[0];
        }

        // Browsers can't set headers on WebSockets, so only the events socket may take the token from the query.
        return Request.Query.TryGetValue(PairingTokenDefaults.QueryKey, out var query)
            && EventsPath().IsMatch(Request.Path.Value ?? string.Empty)
                ? query[0]
                : null;
    }
}
