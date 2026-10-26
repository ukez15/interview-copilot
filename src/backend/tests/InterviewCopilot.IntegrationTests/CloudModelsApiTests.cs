using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using InterviewCopilot.Application.Providers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InterviewCopilot.IntegrationTests;

/// <summary>#30: model listing for cloud providers, and the catalog/preferences that expose them.</summary>
public sealed class CloudModelsApiTests : IClassFixture<ApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _factory;

    public CloudModelsApiTests(ApiFactory factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewKey() => "sk-" + Guid.NewGuid().ToString("N");

    private WebApplicationFactory<Program> WithCatalogs(params ICloudModelCatalog[] catalogs) =>
        _factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.RemoveAll<ICloudModelCatalog>();
            foreach (var catalog in catalogs)
            {
                services.AddSingleton(catalog);
            }
        }));

    private async Task<HttpClient> SignedInAsync(WebApplicationFactory<Program> factory, string username)
    {
        var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/auth/register", _factory.Credentials(username), Ct)).EnsureSuccessStatusCode();
        var signIn = await client.PostAsJsonAsync("/api/auth/sign-in", _factory.Credentials(username), Ct);
        var token = await signIn.Content.ReadFromJsonAsync<ApiFactory.TokenResponse>(Ct);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token!.AccessToken);
        return client;
    }

    private static HttpRequestMessage ModelsRequest(string provider, string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/providers/{provider}/models");
        if (key is not null)
        {
            request.Headers.Add("X-Llm-Api-Key", key);
        }

        return request;
    }

    [Fact]
    public async Task Should_RequireSignIn()
    {
        var response = await _factory.CreateClient().SendAsync(ModelsRequest("openai", NewKey()), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Should_ListModels_WithTheKeyFromTheHeader()
    {
        var key = NewKey();
        var catalog = new StubCloudCatalog("openai") { Models = [new CloudModelInfo("gpt-x", "GPT X")] };
        var client = await SignedInAsync(WithCatalogs(catalog), "cloud-lister");

        var response = await client.SendAsync(ModelsRequest("OpenAI", key), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        body[0].GetProperty("id").GetString().Should().Be("gpt-x");
        body[0].GetProperty("displayName").GetString().Should().Be("GPT X");
        catalog.LastKey.Should().Be(key);
    }

    [Fact]
    public async Task Should_Return404_ForUnknownProvider()
    {
        var client = await SignedInAsync(WithCatalogs(new StubCloudCatalog("openai")), "cloud-unknown");

        var response = await client.SendAsync(ModelsRequest("nope", NewKey()), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Should_Return400_WhenKeyIsMissing()
    {
        var client = await SignedInAsync(WithCatalogs(new StubCloudCatalog("openai")), "cloud-nokey");

        var missing = await client.SendAsync(ModelsRequest("openai", null), Ct);
        var blank = await client.SendAsync(ModelsRequest("openai", " "), Ct);

        missing.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        blank.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await missing.Content.ReadAsStringAsync(Ct)).Should().Contain("Add your API key in Settings");
    }

    [Fact]
    public async Task Should_Return401WithActionableMessage_WhenKeyIsRejected()
    {
        var key = NewKey();
        var catalog = new StubCloudCatalog("anthropic") { Failure = new LlmKeyRejectedException("Anthropic rejected the API key.") };
        var client = await SignedInAsync(WithCatalogs(catalog), "cloud-rejected");

        var response = await client.SendAsync(ModelsRequest("anthropic", key), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadAsStringAsync(Ct);
        body.Should().Contain("Anthropic rejected the API key.").And.NotContain(key);
    }

    [Fact]
    public async Task Should_Return503_WhenProviderIsUnreachable()
    {
        var catalog = new StubCloudCatalog("google") { Failure = new LlmUnavailableException("Can't reach Google Gemini. Check your internet connection.") };
        var client = await SignedInAsync(WithCatalogs(catalog), "cloud-down");

        var response = await client.SendAsync(ModelsRequest("google", NewKey()), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Should_KeepOllamaLiteralRouteWorking()
    {
        var client = await SignedInAsync(
            _factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            {
                s.RemoveAll<IOllamaModelCatalog>();
                s.AddSingleton<IOllamaModelCatalog>(new OllamaStub());
            })),
            "cloud-ollama");

        var response = await client.GetAsync("/api/providers/ollama/models", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("llama3.2:3b");
    }

    [Fact]
    public async Task Should_RegisterTheRealCatalogs_AndListCloudProviders()
    {
        var client = await SignedInAsync(_factory, "cloud-catalog");

        var providers = await client.GetFromJsonAsync<JsonElement>("/api/providers", Json, Ct);
        var llm = providers.GetProperty("llm").EnumerateArray().ToList();

        llm.Select(p => p.GetProperty("name").GetString()).Should().Equal("ollama", "anthropic", "openai", "google");
        llm.Single(p => p.GetProperty("name").GetString() == "ollama").GetProperty("isDefault").GetBoolean().Should().BeTrue();
        foreach (var cloud in llm.Skip(1))
        {
            cloud.GetProperty("isLocal").GetBoolean().Should().BeFalse();
            cloud.GetProperty("requiresApiKey").GetBoolean().Should().BeTrue();
            cloud.GetProperty("isDefault").GetBoolean().Should().BeFalse();
        }

        llm[1].GetProperty("displayName").GetString().Should().Be("Anthropic (cloud, your key)");
        llm[2].GetProperty("displayName").GetString().Should().Be("OpenAI (cloud, your key)");
        llm[3].GetProperty("displayName").GetString().Should().Be("Google Gemini (cloud, your key)");

        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetServices<ICloudModelCatalog>().Select(c => c.Provider)
            .Should().BeEquivalentTo("anthropic", "openai", "google");
    }

    [Fact]
    public async Task Should_SaveACloudProviderChoice()
    {
        var client = await SignedInAsync(_factory, "cloud-choice");

        var put = await client.PutAsJsonAsync("/api/preferences/llm", new { Provider = "openai", Model = "gpt-x" }, Ct);

        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var saved = await put.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        saved.GetProperty("llmProvider").GetString().Should().Be("openai");
        saved.GetProperty("llmModel").GetString().Should().Be("gpt-x");
        saved.GetProperty("defaultLlmProvider").GetString().Should().Be("ollama");
    }

    private sealed class OllamaStub : IOllamaModelCatalog
    {
        public Task<IReadOnlyList<OllamaModelInfo>> ListInstalledAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OllamaModelInfo>>([new OllamaModelInfo("llama3.2:3b", 1, DateTimeOffset.UnixEpoch)]);
    }

    private sealed class StubCloudCatalog(string provider) : ICloudModelCatalog
    {
        public string Provider => provider;

        public IReadOnlyList<CloudModelInfo> Models { get; init; } = [];

        public Exception? Failure { get; init; }

        public string? LastKey { get; private set; }

        public Task<IReadOnlyList<CloudModelInfo>> ListAsync(string apiKey, CancellationToken cancellationToken)
        {
            LastKey = apiKey;
            return Failure is null ? Task.FromResult(Models) : throw Failure;
        }
    }
}
