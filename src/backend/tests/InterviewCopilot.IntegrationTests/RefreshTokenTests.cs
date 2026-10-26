using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace InterviewCopilot.IntegrationTests;

public sealed class RefreshTokenTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public RefreshTokenTests(ApiFactory factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_IssueWorkingTokens_When_RefreshTokenIsValid()
    {
        var client = _factory.CreateClient();
        (await client.PostAsJsonAsync("/api/auth/register", _factory.Credentials("refresher"), Ct)).EnsureSuccessStatusCode();
        var signIn = await (await client.PostAsJsonAsync("/api/auth/sign-in", _factory.Credentials("refresher"), Ct))
            .Content.ReadFromJsonAsync<Tokens>(Ct);

        var response = await client.PostAsJsonAsync("/api/auth/refresh", new { signIn!.RefreshToken }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var refreshed = await response.Content.ReadFromJsonAsync<Tokens>(Ct);
        refreshed!.AccessToken.Should().NotBeNullOrEmpty();
        refreshed.RefreshToken.Should().NotBeNullOrEmpty();
        using var authed = _factory.CreateClient();
        authed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", refreshed.AccessToken);
        (await authed.GetAsync("/api/sessions", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("not-a-real-token")]
    [InlineData("")]
    public async Task Should_Reject_When_RefreshTokenIsGarbage(string token)
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/auth/refresh", new { RefreshToken = token }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Should_Reject_When_RefreshTokenIsExpired()
    {
        var options = _factory.Services.GetRequiredService<IOptionsMonitor<BearerTokenOptions>>().Get(BearerTokenDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "expired")], BearerTokenDefaults.AuthenticationScheme));
        var properties = new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var expired = options.RefreshTokenProtector.Protect(new AuthenticationTicket(principal, properties, BearerTokenDefaults.AuthenticationScheme));

        var response = await _factory.CreateClient().PostAsJsonAsync("/api/auth/refresh", new { RefreshToken = expired }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private sealed record Tokens(string AccessToken, string RefreshToken);
}
