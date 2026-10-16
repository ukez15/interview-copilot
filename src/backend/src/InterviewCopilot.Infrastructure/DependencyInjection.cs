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

        services.AddSingleton<IPasswordHasher, AspNetPasswordHasher>();
        services.AddSingleton<IDocumentTextExtractor, PlainTextExtractor>();
        services.AddSingleton<IDocumentTextExtractor, PdfTextExtractor>();
        services.AddSingleton<IDocumentTextExtractor, DocxTextExtractor>();
        services.AddSingleton<IPdfExporter, QuestPdfTranscriptExporter>();

        services.AddHttpClient<ILlmProvider, OllamaLlmProvider>((provider, http) =>
        {
            http.BaseAddress = provider.GetRequiredService<IOptions<LlmOptions>>().Value.Ollama.BaseUrl;
            http.Timeout = Timeout.InfiniteTimeSpan; // streaming; cancellation comes from the request
        });

        services.AddHttpClient<IOllamaModelCatalog, OllamaModelCatalog>((provider, http) =>
        {
            http.BaseAddress = provider.GetRequiredService<IOptions<LlmOptions>>().Value.Ollama.BaseUrl;
            http.Timeout = TimeSpan.FromSeconds(5);
        });

        AddSpeechToText(services, configuration);
        return services;
    }

    private static void AddSpeechToText(IServiceCollection services, IConfiguration configuration)
    {
        var transcription = configuration.GetSection(TranscriptionOptions.SectionName).Get<TranscriptionOptions>()
            ?? new TranscriptionOptions();

        var useDeepgram = transcription.Providers.Deepgram.Enabled
            && string.Equals(transcription.Provider, ProviderNames.Deepgram, StringComparison.OrdinalIgnoreCase);

        if (useDeepgram)
        {
            services.AddHttpClient<ISpeechToTextProvider, DeepgramSpeechToTextProvider>();
        }
        else
        {
            services.AddSingleton<ISpeechToTextProvider, WhisperSpeechToTextProvider>();
        }

        services.AddSingleton<IWhisperModelSource, GgmlWhisperModelSource>();
        services.AddSingleton<WhisperModelManager>();
        services.AddSingleton<IWhisperModelStatus>(provider => provider.GetRequiredService<WhisperModelManager>());
    }
}
