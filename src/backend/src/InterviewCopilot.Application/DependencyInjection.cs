using InterviewCopilot.Application.Audio;
using InterviewCopilot.Application.Auth;
using InterviewCopilot.Application.Copilot;
using InterviewCopilot.Application.Documents;
using InterviewCopilot.Application.Events;
using InterviewCopilot.Application.Pairing;
using InterviewCopilot.Application.Practice;
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
        services.AddSingleton<ISessionKeyStore, InMemorySessionKeyStore>();
        services.AddTransient<ILlmProviderResolver, LlmProviderResolver>();
        services.AddScoped<SessionService>();
        services.AddScoped<DocumentService>();
        services.AddScoped<JobPostingImportService>();
        services.AddScoped<AccountService>();
        services.AddSingleton<PairingCodeStore>();
        services.AddScoped<PairingService>();
        services.AddScoped<TranscriptService>();
        services.AddScoped<AnswerGenerator>();
        services.AddScoped<PreferencesService>();
        services.AddSingleton<AutoAnswerService>();
        services.AddSingleton<ITranscriptSegmentListener>(provider => provider.GetRequiredService<AutoAnswerService>());

        services.AddScoped<PracticeService>();
        services.AddSingleton<PracticeAnswerCollector>();
        services.AddSingleton<ITranscriptSegmentListener>(provider => provider.GetRequiredService<PracticeAnswerCollector>());
        services.AddSingleton<IAudioCaptureGate, PracticeCaptureGate>(); // #78: no mock-interview transcription between questions
        services.AddSingleton<IAudioProgressListener, PracticeSilenceWatcher>(); // #78: score after 6 s without voice

        services.AddSingleton<AudioStatsSink>();
        services.AddSingleton<IAudioFrameSink>(provider => provider.GetRequiredService<AudioStatsSink>());
        services.AddSingleton<ISpeechToTextRouter, SpeechToTextRouter>();
        services.AddSingleton<TranscriptionSink>();
        services.AddSingleton<IAudioFrameSink>(provider => provider.GetRequiredService<TranscriptionSink>());
        services.AddSingleton<IAudioStreamClock>(provider => provider.GetRequiredService<TranscriptionSink>());
        services.AddSingleton<DeepgramStreamingSink>();
        services.AddSingleton<IAudioFrameSink>(provider => provider.GetRequiredService<DeepgramStreamingSink>());
        services.AddHostedService<TranscriptionWorker>();
        services.AddScoped<AudioIngestService>();

        // Replaced by the events hub (#23); TryAdd so a later registration wins.
        services.TryAddSingleton<ISessionEventPublisher, NullSessionEventPublisher>();
        return services;
    }
}
