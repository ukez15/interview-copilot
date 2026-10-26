using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using FluentAssertions;
using InterviewCopilot.Application.Pairing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InterviewCopilot.IntegrationTests;

/// <summary>Pairing a browser extension with the desktop app (#33), end to end. Each test gets its own host so rate-limit state never leaks.</summary>
public sealed class PairingApiTests
{
    private const string PairingHeader = "X-Pairing-Token";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<HttpClient> SignInAsync(WebApplicationFactory<Program> host, ApiFactory credentials, string username)
    {
        var client = host.CreateClient();
        (await client.PostAsJsonAsync("/api/auth/register", credentials.Credentials(username), Ct)).EnsureSuccessStatusCode();
        var signIn = await client.PostAsJsonAsync("/api/auth/sign-in", credentials.Credentials(username), Ct);
        var token = await signIn.Content.ReadFromJsonAsync<ApiFactory.TokenResponse>(Ct);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);
        return client;
    }

    private static async Task<string> IssueCodeAsync(HttpClient owner)
    {
        var response = await owner.PostAsync("/api/pairing/codes", content: null, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        body.RootElement.GetProperty("expiresAt").GetDateTimeOffset().Should().BeAfter(DateTimeOffset.UtcNow);
        return body.RootElement.GetProperty("code").GetString()!;
    }

    private static Task<HttpResponseMessage> ClaimAsync(HttpClient anonymous, string code, string name = "Chrome") =>
        anonymous.PostAsJsonAsync("/api/pairing/claim", new { Code = code, Name = name }, Ct);

    private static async Task<string> PairAsync(HttpClient owner, HttpClient anonymous, string name = "Chrome")
    {
        var response = await ClaimAsync(anonymous, await IssueCodeAsync(owner), name);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return body.RootElement.GetProperty("token").GetString()!;
    }

    private static HttpRequestMessage WithToken(HttpMethod method, string url, string token, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Add(PairingHeader, token);
        return request;
    }

    private static async Task<Guid> CreateSessionAsync(HttpClient owner)
    {
        var response = await owner.PostAsJsonAsync(
            "/api/sessions",
            new { Title = "Pairing", Mode = "LiveInterview", AnswerLength = "Short", Framework = "None" },
            Ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Should_AllowPairingToken_OnTheThreeExtensionEndpoints_And_StillAllowBearer()
    {
        using var factory = new ApiFactory();
        var owner = await SignInAsync(factory, factory, "pair-scope");
        var sessionId = await CreateSessionAsync(owner);
        var token = await PairAsync(owner, factory.CreateClient());
        var extension = factory.CreateClient();

        var list = await extension.SendAsync(WithToken(HttpMethod.Get, "/api/sessions", token), Ct);
        var answer = await extension.SendAsync(
            WithToken(HttpMethod.Post, $"/api/sessions/{sessionId}/answers", token, JsonContent.Create(new { Question = "Why us?" })),
            Ct);
        using var socket = await factory.Server.CreateWebSocketClient().ConnectAsync(
            new Uri(factory.Server.BaseAddress, $"ws/sessions/{sessionId}/events?pairing_token={Uri.EscapeDataString(token)}"),
            Ct);

        list.StatusCode.Should().Be(HttpStatusCode.OK);
        (await list.Content.ReadAsStringAsync(Ct)).Should().Contain(sessionId.ToString());
        answer.StatusCode.Should().Be(HttpStatusCode.OK);
        socket.State.Should().Be(WebSocketState.Open);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", Ct);

        (await owner.GetAsync("/api/sessions", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await owner.PostAsJsonAsync($"/api/sessions/{sessionId}/answers", new { Question = "Why us?" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("GET", "/api/preferences")]
    [InlineData("PUT", "/api/preferences/speech-to-text")]
    [InlineData("POST", "/api/pairing/codes")]
    [InlineData("GET", "/api/pairing/browsers")]
    [InlineData("POST", "/api/sessions")]
    [InlineData("GET", "/api/storage")]
    [InlineData("DELETE", "/api/sessions/00000000-0000-0000-0000-000000000001/documents/00000000-0000-0000-0000-000000000002")]
    public async Task Should_RejectPairingToken_OnEveryOtherEndpoint(string method, string url)
    {
        using var factory = new ApiFactory();
        var owner = await SignInAsync(factory, factory, "pair-reject");
        var token = await PairAsync(owner, factory.CreateClient());

        var response = await factory.CreateClient().SendAsync(
            WithToken(new HttpMethod(method), url, token, method is "GET" or "DELETE" ? null : JsonContent.Create(new { })),
            Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Should_RejectPairingToken_OnSessionDetail_And_OnQueryStringOutsideTheEventsSocket()
    {
        using var factory = new ApiFactory();
        var owner = await SignInAsync(factory, factory, "pair-detail");
        var sessionId = await CreateSessionAsync(owner);
        var token = await PairAsync(owner, factory.CreateClient());
        var extension = factory.CreateClient();

        var detail = await extension.SendAsync(WithToken(HttpMethod.Get, $"/api/sessions/{sessionId}", token), Ct);
        var queryOnList = await extension.GetAsync($"/api/sessions?pairing_token={Uri.EscapeDataString(token)}", Ct);

        detail.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        queryOnList.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Should_Return401_When_PairingTokenIsUnknown_Or_Missing()
    {
        using var factory = new ApiFactory();
        var extension = factory.CreateClient();

        var unknown = await extension.SendAsync(WithToken(HttpMethod.Get, "/api/sessions", "not-a-real-token"), Ct);
        var missing = await extension.GetAsync("/api/sessions", Ct);

        unknown.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        missing.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Should_Return401_And_HideBrowser_When_Revoked()
    {
        using var factory = new ApiFactory();
        var owner = await SignInAsync(factory, factory, "pair-revoke");
        var extension = factory.CreateClient();
        var token = await PairAsync(owner, extension, "Edge");
        (await extension.SendAsync(WithToken(HttpMethod.Get, "/api/sessions", token), Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var browsers = await owner.GetFromJsonAsync<List<PairedBrowserDto>>("/api/pairing/browsers", Ct);
        var browser = browsers.Should().ContainSingle().Subject;
        browser.Name.Should().Be("Edge");
        browser.LastUsedAt.Should().NotBeNull();

        var revoke = await owner.DeleteAsync($"/api/pairing/browsers/{browser.Id}", Ct);

        revoke.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await extension.SendAsync(WithToken(HttpMethod.Get, "/api/sessions", token), Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await owner.GetFromJsonAsync<List<PairedBrowserDto>>("/api/pairing/browsers", Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Should_Return404_When_RevokingAnotherUsersBrowser()
    {
        using var factory = new ApiFactory();
        var owner = await SignInAsync(factory, factory, "pair-owner");
        var stranger = await SignInAsync(factory, factory, "pair-stranger");
        var token = await PairAsync(owner, factory.CreateClient());
        var browser = (await owner.GetFromJsonAsync<List<PairedBrowserDto>>("/api/pairing/browsers", Ct))!.Single();

        var response = await stranger.DeleteAsync($"/api/pairing/browsers/{browser.Id}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await factory.CreateClient().SendAsync(WithToken(HttpMethod.Get, "/api/sessions", token), Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await stranger.GetFromJsonAsync<List<PairedBrowserDto>>("/api/pairing/browsers", Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Should_RequireSignIn_ToIssueCodeListOrRevoke()
    {
        using var factory = new ApiFactory();
        var anonymous = factory.CreateClient();

        (await anonymous.PostAsync("/api/pairing/codes", content: null, Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync("/api/pairing/browsers", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.DeleteAsync($"/api/pairing/browsers/{Guid.NewGuid()}", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Should_Return400_When_CodeIsReused()
    {
        using var factory = new ApiFactory();
        var owner = await SignInAsync(factory, factory, "pair-reuse");
        var anonymous = factory.CreateClient();
        var code = await IssueCodeAsync(owner);
        (await ClaimAsync(anonymous, code)).StatusCode.Should().Be(HttpStatusCode.OK);

        var again = await ClaimAsync(anonymous, code);

        again.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await again.Content.ReadAsStringAsync(Ct)).Should().Contain(PairingService.InvalidCodeMessage);
    }

    [Fact]
    public async Task Should_Return400_When_CodeExpired_Or_Replaced()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        using var original = new ApiFactory();
        using var factory = original.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock))));
        var owner = await SignInAsync(factory, original, "pair-expire");
        var anonymous = factory.CreateClient();
        var expiring = await IssueCodeAsync(owner);
        clock.Advance(PairingCodeStore.Lifetime);

        var expired = await ClaimAsync(anonymous, expiring);

        expired.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await expired.Content.ReadAsStringAsync(Ct)).Should().Contain(PairingService.InvalidCodeMessage);

        var old = await IssueCodeAsync(owner);
        var current = await IssueCodeAsync(owner);
        if (old != current)
        {
            (await ClaimAsync(anonymous, old)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        (await ClaimAsync(anonymous, current)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Should_Return400_When_CodeIsUnknown_Or_NameIsMissing()
    {
        using var factory = new ApiFactory();
        var owner = await SignInAsync(factory, factory, "pair-bad");
        var anonymous = factory.CreateClient();
        var code = await IssueCodeAsync(owner);

        var unknown = await ClaimAsync(anonymous, code == "000000" ? "111111" : "000000");
        var noName = await ClaimAsync(anonymous, code, " ");

        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        noName.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Should_Return429_On_SixthClaimWithinAMinute()
    {
        using var factory = new ApiFactory();
        var anonymous = factory.CreateClient();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            (await ClaimAsync(anonymous, "000000")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        var sixth = await ClaimAsync(anonymous, "000000");

        sixth.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
