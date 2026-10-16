using InterviewCopilot.Application.Audio;
using InterviewCopilot.Application.Auth;
using InterviewCopilot.Application.Copilot;
using InterviewCopilot.Application.Documents;
using InterviewCopilot.Application.Providers;
using InterviewCopilot.Application.Sessions;
using InterviewCopilot.Application.Transcripts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace InterviewCopilot.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TranscriptionOptions>(configuration.GetSection(TranscriptionOptions.SectionName));
        services.Configure<LlmOptions>(configuration.GetSection(LlmOptions.SectionName));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ProviderCatalog>();
        services.AddScoped<SessionService>();
        services.AddScoped<DocumentService>();
        services.AddScoped<AccountService>();
        services.AddScoped<TranscriptService>();
        services.AddScoped<AnswerGenerator>();

        services.AddSingleton<AudioStatsSink>();
        services.AddSingleton<IAudioFrameSink>(provider => provider.GetRequiredService<AudioStatsSink>());
        services.AddScoped<AudioIngestService>();
        return services;
    }
}
