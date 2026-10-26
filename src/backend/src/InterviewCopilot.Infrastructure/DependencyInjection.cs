using InterviewCopilot.Application.Audio;
using InterviewCopilot.Application.Auth;
using InterviewCopilot.Application.Common.Interfaces;
using InterviewCopilot.Application.Documents;
using InterviewCopilot.Application.Providers;
using InterviewCopilot.Application.Transcripts;
using InterviewCopilot.Infrastructure.Documents;
using InterviewCopilot.Infrastructure.Llm;
using InterviewCopilot.Infrastructure.Pdf;
using InterviewCopilot.Infrastructure.Persistence;
using InterviewCopilot.Infrastructure.Security;
using InterviewCopilot.Infrastructure.Transcription;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace InterviewCopilot.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.SectionName));

        services.AddDbContext<InterviewCopilotDbContext>((provider, options) =>
        {
            var storage = provider.GetRequiredService<IOptions<StorageOptions>>().Value;
            var connectionString = configuration.GetConnectionString("InterviewCopilot");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                Directory.CreateDirectory(storage.ResolveDataDirectory());
                connectionString = $"Data Source={storage.ResolveDatabasePath()}";
            }

            options.UseSqlite(connectionString);
        });
        services.AddScoped<IUnitOfWork>(provider => provider.GetRequiredService<InterviewCopilotDbContext>());
        services.AddScoped<ISessionRepository, SessionRepository>();
        services.AddScoped<IUserAccountRepository, UserAccountRepository>();
        services.AddScoped<ITranscriptRepository, TranscriptRepository>();
        services.AddScoped<IUserPreferencesRepository, UserPreferencesRepository>();
        services.AddScoped<IPairedBrowserRepository, PairedBrowserRepository>();
        services.AddScoped<IPracticeAttemptRepository, PracticeAttemptRepository>();

        services.AddSingleton<IPasswordHasher, AspNetPasswordHasher>();
        services.AddSingleton<IDocumentTextExtractor, PlainTextExtractor>();
        services.AddSingleton<IDocumentTextExtractor, PdfTextExtractor>();
        services.AddSingleton<IDocumentTextExtractor, DocxTextExtractor>();
        services.AddSingleton<IPdfExporter, QuestPdfTranscriptExporter>();

        // Each chat provider is a typed client of its own class (so their HttpClient settings don't merge)
        // and is also exposed as ILlmProvider for ILlmProviderResolver. BYOK providers (#30) follow suit.
        services.AddHttpClient<OllamaLlmProvider>((provider, http) =>
        {
            http.BaseAddress = provider.GetRequiredService<IOptions<LlmOptions>>().Value.Ollama.BaseUrl;
            http.Timeout = Timeout.InfiniteTimeSpan; // streaming; cancellation comes from the request
        });
        services.AddTransient<ILlmProvider>(provider => provider.GetRequiredService<OllamaLlmProvider>());

        services.AddHttpClient<IOllamaModelCatalog, OllamaModelCatalog>((provider, http) =>
        {
            http.BaseAddress = provider.GetRequiredService<IOptions<LlmOptions>>().Value.Ollama.BaseUrl;
            http.Timeout = TimeSpan.FromSeconds(5);
        });

        AddCloudLlm<AnthropicLlmProvider, AnthropicModelCatalog>(services, o => o.Anthropic.BaseUrl);
        AddCloudLlm<OpenAiLlmProvider, OpenAiModelCatalog>(services, o => o.OpenAi.BaseUrl);
        AddCloudLlm<GoogleLlmProvider, GoogleModelCatalog>(services, o => o.Google.BaseUrl);

        AddSpeechToText(services);
        return services;
    }

    /// <summary>BYOK chat provider (answers stream, so no client timeout) plus its model lister (10 s).</summary>
    private static void AddCloudLlm<TProvider, TCatalog>(IServiceCollection services, Func<LlmOptions, Uri> baseUrl)
        where TProvider : class, ILlmProvider
        where TCatalog : class, ICloudModelCatalog
    {
        services.AddHttpClient<TProvider>((provider, http) =>
        {
            http.BaseAddress = baseUrl(provider.GetRequiredService<IOptions<LlmOptions>>().Value);
            http.Timeout = Timeout.InfiniteTimeSpan; // streaming; cancellation comes from the request
        });
        services.AddTransient<ILlmProvider>(provider => provider.GetRequiredService<TProvider>());

        services.AddHttpClient<TCatalog>((provider, http) =>
        {
            http.BaseAddress = baseUrl(provider.GetRequiredService<IOptions<LlmOptions>>().Value);
            http.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddTransient<ICloudModelCatalog>(provider => provider.GetRequiredService<TCatalog>());
    }

    private static void AddSpeechToText(IServiceCollection services)
    {
        // Whisper is always the batch provider. Deepgram is streaming-only (DeepgramStreamingSink); the factory
        // below only opens a socket when the sink asks, and the sink asks only for sessions routed to Deepgram.
        services.AddSingleton<ISpeechToTextProvider, WhisperSpeechToTextProvider>();
        services.AddSingleton<IDeepgramConnectionFactory, ClientWebSocketDeepgramConnectionFactory>();
        services.AddSingleton<IWhisperModelSource, GgmlWhisperModelSource>();
        services.AddSingleton<WhisperModelManager>();
        services.AddSingleton<IWhisperModelStatus>(provider => provider.GetRequiredService<WhisperModelManager>());
    }
}
