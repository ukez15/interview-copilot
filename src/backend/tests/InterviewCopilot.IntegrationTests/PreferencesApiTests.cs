using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using InterviewCopilot.Application.Providers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InterviewCopilot.IntegrationTests;

public sealed class PreferencesApiTests : IClassFixture<ApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _factory;

    public PreferencesApiTests(ApiFactory factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpClient> SignedInAsync(WebApplicationFactory<Program> factory, string username)
    {
        var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/auth/register", _factory.Credentials(username), Ct)).EnsureSuccessStatusCode();
        var signIn = await client.PostAsJsonAsync("/api/auth/sign-in", _factory.Credentials(username), Ct);
        var token = await signIn.Content.ReadFromJsonAsync<ApiFactory.TokenResponse>(Ct);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);
        return client;
    }

    private WebApplicationFactory<Program> WithCatalog(IOllamaModelCatalog catalog) =>
        _factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.RemoveAll<IOllamaModelCatalog>();
            services.AddSingleton(catalog);
        }));

    [Theory]
    [InlineData("GET", "/api/providers/ollama/models")]
    [InlineData("GET", "/api/preferences")]
    [InlineData("PUT", "/api/preferences")]
    public async Task Should_RequireSignIn(string method, string url)
    {
        var response = await _factory.CreateClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), url), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Should_ListInstalledModels()
    {
        var catalog = new StubCatalog { Models = [new OllamaModelInfo("llama3.2:3b", 2_000, new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero))] };
        var client = await SignedInAsync(WithCatalog(catalog), "lister");

        var response = await client.GetAsync("/api/providers/ollama/models", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(Ct);
        using var doc = JsonDocument.Parse(body);
        var first = doc.RootElement[0];
        first.GetProperty("name").GetString().Should().Be("llama3.2:3b");
        first.GetProperty("sizeBytes").GetInt64().Should().Be(2_000);
        first.GetProperty("modifiedAt").GetDateTimeOffset().Year.Should().Be(2026);
    }

    [Fact]
    public async Task Should_Return503WithActionableMessage_When_OllamaIsDown()
    {
        var client = await SignedInAsync(WithCatalog(new StubCatalog { Fail = true }), "offline");

        var response = await client.GetAsync("/api/providers/ollama/models", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("Can't reach Ollama. Make sure it's installed and running.");
    }

    [Fact]
    public async Task Should_SaveAndReturnPreferredModelPerAccount()
    {
        var client = await SignedInAsync(_factory, "chooser");
        var other = await SignedInAsync(_factory, "bystander");

        (await ReadModelAsync(client)).Should().BeNull();
        var put = await client.PutAsJsonAsync("/api/preferences", new { llmModel = "qwen2.5:7b" }, Ct);
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        (await ReadModelAsync(client)).Should().Be("qwen2.5:7b");
        (await ReadModelAsync(other)).Should().BeNull();

        (await client.PutAsJsonAsync("/api/preferences", new { llmModel = "  " }, Ct)).EnsureSuccessStatusCode();
        (await ReadModelAsync(client)).Should().BeNull();
    }

    [Fact]
    public async Task Should_Return400_When_ModelNameTooLong()
    {
        var client = await SignedInAsync(_factory, "verbose");

        var response = await client.PutAsJsonAsync("/api/preferences", new { llmModel = new string('x', 201) }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static async Task<string?> ReadModelAsync(HttpClient client)
    {
        var prefs = await client.GetFromJsonAsync<JsonElement>("/api/preferences", Json, Ct);
        return prefs.GetProperty("llmModel").GetString();
    }

    private sealed class StubCatalog : IOllamaModelCatalog
    {
        public IReadOnlyList<OllamaModelInfo> Models { get; init; } = [];

        public bool Fail { get; init; }

        public Task<IReadOnlyList<OllamaModelInfo>> ListInstalledAsync(CancellationToken cancellationToken) =>
            Fail
                ? throw new LlmUnavailableException("Can't reach Ollama. Make sure it's installed and running.")
                : Task.FromResult(Models);
    }
}
