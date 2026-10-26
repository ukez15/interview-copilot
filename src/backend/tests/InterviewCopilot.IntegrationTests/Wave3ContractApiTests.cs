using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using InterviewCopilot.Application.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace InterviewCopilot.IntegrationTests;

/// <summary>Step-0 endpoints for wave 3 (docs/atlas-gotcha/wave-3.md).</summary>
public sealed class Wave3ContractApiTests : IClassFixture<ApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _factory;

    public Wave3ContractApiTests(ApiFactory factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpClient> SignedInAsync(string username)
    {
        var client = _factory.CreateClient();
        (await client.PostAsJsonAsync("/api/auth/register", _factory.Credentials(username), Ct)).EnsureSuccessStatusCode();
        var signIn = await client.PostAsJsonAsync("/api/auth/sign-in", _factory.Credentials(username), Ct);
        var token = await signIn.Content.ReadFromJsonAsync<ApiFactory.TokenResponse>(Ct);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);
        return client;
    }

    private static async Task<Guid> CreateSessionAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/sessions",
            new { Title = "Keys", Mode = "LiveInterview", AnswerLength = "Short", Framework = "None" },
            Ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    [Theory]
    [InlineData("/api/preferences/llm")]
    [InlineData("/api/preferences/speech-to-text")]
    [InlineData("/api/sessions/00000000-0000-0000-0000-000000000001/keys/openai")]
    public async Task Should_RequireSignIn(string url)
    {
        var response = await _factory.CreateClient().PutAsJsonAsync(url, new { }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Should_SaveProviderChoices_And_RejectUnavailableOnes()
    {
        var client = await SignedInAsync("wave3-prefs");

        var llm = await client.PutAsJsonAsync("/api/preferences/llm", new { Provider = "ollama", Model = "qwen2.5:3b" }, Ct);
        llm.StatusCode.Should().Be(HttpStatusCode.OK);
        var saved = await llm.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        saved.GetProperty("llmProvider").GetString().Should().Be("ollama");
        saved.GetProperty("llmModel").GetString().Should().Be("qwen2.5:3b");
        saved.GetProperty("defaultSpeechToTextProvider").GetString().Should().Be("whisper");

        var stt = await client.PutAsJsonAsync("/api/preferences/speech-to-text", new { Provider = "whisper" }, Ct);
        stt.StatusCode.Should().Be(HttpStatusCode.OK);

        var disabled = await client.PutAsJsonAsync("/api/preferences/speech-to-text", new { Provider = "deepgram" }, Ct);
        disabled.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Should_AcceptKeyOnlyForLiveSession_And_ClearItOnEnd()
    {
        var client = await SignedInAsync("wave3-keys");
        var sessionId = await CreateSessionAsync(client);
        var url = $"/api/sessions/{sessionId}/keys/openai";
        var keys = _factory.Services.GetRequiredService<ISessionKeyStore>();

        (await client.PutAsJsonAsync(url, new { Key = "sk-test" }, Ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await client.PostAsync($"/api/sessions/{sessionId}/start", null, Ct)).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync(url, new { Key = "sk-test" }, Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        keys.TryGet(sessionId, "openai").Should().Be("sk-test");

        (await client.PutAsJsonAsync($"/api/sessions/{sessionId}/keys/ollama", new { Key = "x" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await client.PostAsync($"/api/sessions/{sessionId}/end", null, Ct)).EnsureSuccessStatusCode();
        keys.TryGet(sessionId, "openai").Should().BeNull();
    }
}
