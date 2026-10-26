using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using InterviewCopilot.Application.Documents;
using InterviewCopilot.Application.Providers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InterviewCopilot.IntegrationTests;

/// <summary>Runs the real API against a private in-memory SQLite DB and a scripted LLM (no Ollama needed).</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString = $"Data Source=file:test-{Guid.NewGuid():N}?mode=memory&cache=shared";
    private readonly SqliteConnection _keepAlive;

    public ApiFactory()
    {
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
    }

    /// <summary>Random per test run; never a real credential.</summary>
    public string TestSecret { get; } = $"test-{Guid.NewGuid():N}";

    public ScriptedLlm Llm { get; } = new();

    public ScriptedStt Stt { get; } = new();

    public FakeWhisperModel WhisperModel { get; } = new();

    public FakeWebPageFetcher Fetcher { get; } = new();

    public object Credentials(string username, string? secret = null) =>
        new Dictionary<string, string> { ["username"] = username, ["password"] = secret ?? TestSecret };

    public async Task<HttpClient> CreateSignedInClientAsync(string username = "candidate")
    {
        var client = CreateClient();
        (await client.PostAsJsonAsync("/api/auth/register", Credentials(username))).EnsureSuccessStatusCode();
        var signIn = await client.PostAsJsonAsync("/api/auth/sign-in", Credentials(username));
        signIn.EnsureSuccessStatusCode();
        var token = await signIn.Content.ReadFromJsonAsync<TokenResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:InterviewCopilot", _connectionString);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ILlmProvider>();
            services.AddSingleton<ILlmProvider>(Llm);
            services.RemoveAll<ISpeechToTextProvider>();
            services.AddSingleton<ISpeechToTextProvider>(Stt);
            services.RemoveAll<IWhisperModelStatus>();
            services.AddSingleton<IWhisperModelStatus>(WhisperModel);
            services.RemoveAll<IWebPageFetcher>();
            services.AddSingleton<IWebPageFetcher>(Fetcher);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _keepAlive.Dispose();
        }
    }

    public sealed record TokenResponse(string AccessToken);
}

public sealed class ScriptedLlm : ILlmProvider
{
    public string[] Tokens { get; set; } = ["Suggested ", "answer."];

    public bool Fail { get; set; }

    public string Name => ProviderNames.Ollama; // stands in for the configured default provider

    public async IAsyncEnumerable<string> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Fail)
        {
            throw new LlmUnavailableException("Can't reach Ollama.");
        }

        foreach (var token in Tokens)
        {
            await Task.Yield();
            yield return token;
        }
    }
}

/// <summary>Stands in for the Whisper model manager so no test downloads anything.</summary>
public sealed class FakeWhisperModel : IWhisperModelStatus
{
    public WhisperModelState State { get; set; } = WhisperModelState.Missing;

    public WhisperModelStatus GetStatus() => new(State, "ggml-small.en.bin", 5, 10, null);

    public WhisperDownloadStart StartDownload()
    {
        switch (State)
        {
            case WhisperModelState.Downloading:
                return WhisperDownloadStart.AlreadyDownloading;
            case WhisperModelState.Ready:
                return WhisperDownloadStart.AlreadyReady;
            default:
                State = WhisperModelState.Downloading;
                return WhisperDownloadStart.Started;
        }
    }
}

/// <summary>Stands in for Whisper: returns a scripted transcript (or fails) without loading a model.</summary>
public sealed class ScriptedStt : ISpeechToTextProvider
{
    public string Text { get; set; } = "Tell me about a time you led a team.";

    public bool Fail { get; set; }

    public string Name => ProviderNames.Ollama; // stands in for the configured default provider

    public Task<TranscriptionResult> TranscribeAsync(AudioChunk chunk, string? apiKey, CancellationToken cancellationToken)
    {
        if (Fail)
        {
            throw new InvalidOperationException("Speech model not available.");
        }

        return Task.FromResult(new TranscriptionResult(Text));
    }
}

/// <summary>Stands in for the real fetcher so no test touches the network.</summary>
public sealed class FakeWebPageFetcher : IWebPageFetcher
{
    public Func<Uri, FetchedPage> Respond { get; set; } = url => new FetchedPage(url, "<html><body></body></html>");

    public List<Uri> Requested { get; } = [];

    public Task<FetchedPage> FetchAsync(Uri url, CancellationToken cancellationToken)
    {
        Requested.Add(url);
        return Task.FromResult(Respond(url));
    }
}
