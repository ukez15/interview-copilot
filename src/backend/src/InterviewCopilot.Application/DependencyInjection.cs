using InterviewCopilot.Application.Audio;
using InterviewCopilot.Application.Auth;
using InterviewCopilot.Application.Copilot;
using InterviewCopilot.Application.Documents;
using InterviewCopilot.Application.Events;
using InterviewCopilot.Application.Preferences;
using InterviewCopilot.Application.Providers;
using InterviewCopilot.Application.Sessions;
using InterviewCopilot.Application.Transcripts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InterviewCopilot.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TranscriptionOptions>(configuration.GetSection(TranscriptionOptions.SectionName));
        services.Configure<VadOptions>(configuration.GetSection(VadOptions.SectionName));
        services.Configure<LlmOptions>(configuration.GetSection(LlmOptions.SectionName));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ProviderCatalog>();
        services.AddScoped<SessionService>();
        services.AddScoped<DocumentService>();
        services.AddScoped<AccountService>();
        services.AddScoped<TranscriptService>();
        services.AddScoped<AnswerGenerator>();
        services.AddScoped<PreferencesService>();

        services.AddSingleton<AudioStatsSink>();
        services.AddSingleton<IAudioFrameSink>(provider => provider.GetRequiredService<AudioStatsSink>());
        services.AddSingleton<TranscriptionSink>();
        services.AddSingleton<IAudioFrameSink>(provider => provider.GetRequiredService<TranscriptionSink>());
        services.AddHostedService<TranscriptionWorker>();
        services.AddScoped<AudioIngestService>();

        // Replaced by the events hub (#23); TryAdd so a later registration wins.
        services.TryAddSingleton<ISessionEventPublisher, NullSessionEventPublisher>();
        return services;
    }
}
