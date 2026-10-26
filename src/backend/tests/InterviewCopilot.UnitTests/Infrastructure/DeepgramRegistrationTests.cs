using FluentAssertions;
using InterviewCopilot.Application;
using InterviewCopilot.Application.Audio;
using InterviewCopilot.Application.Providers;
using InterviewCopilot.Infrastructure;
using InterviewCopilot.UnitTests.Audio;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace InterviewCopilot.UnitTests.Infrastructure;

public sealed class DeepgramRegistrationTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly FakeDeepgramFactory _factory = new();

    public DeepgramRegistrationTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Transcription:Provider"] = ProviderNames.Deepgram,
                ["Transcription:Providers:Deepgram:Enabled"] = "false",
                ["ConnectionStrings:InterviewCopilot"] = "Data Source=:memory:",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplication(configuration);
        services.AddInfrastructure(configuration);
        services.AddSingleton<IDeepgramConnectionFactory>(_factory);
        _provider = services.BuildServiceProvider();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _provider.Dispose();

    [Fact]
    public void Should_RegisterWhisperAsTheOnlyBatchProvider_EvenWhenTheDefaultNamesDeepgram()
    {
        _provider.GetServices<ISpeechToTextProvider>().Should().ContainSingle().Which.Name.Should().Be(ProviderNames.Whisper);
    }

    [Fact]
    public async Task Should_NeverOpenASocket_When_DeepgramIsDisabledInConfig()
    {
        var sinks = _provider.GetServices<IAudioFrameSink>().ToList();
        sinks.OfType<DeepgramStreamingSink>().Should().ContainSingle();
        var key = _provider.GetRequiredService<ISessionKeyStore>();
        var session = Guid.NewGuid();
        key.Store(session, ProviderNames.Deepgram, "test-key-value");

        foreach (var sink in sinks.Where(s => s is DeepgramStreamingSink))
        {
            await sink.OnFrameAsync(session, new AudioFrame(InterviewCopilot.Domain.Enums.Speaker.Interviewer, new byte[3_200]), Ct);
            await sink.OnStreamEndedAsync(session, Ct);
        }

        _factory.ConnectAttempts.Should().Be(0);
    }

    [Fact]
    public void Should_RegisterTheRealSocketFactory_ByDefault()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        services.AddLogging();
        services.AddApplication(configuration);
        services.AddInfrastructure(configuration);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IDeepgramConnectionFactory>().GetType().Name.Should().Be("ClientWebSocketDeepgramConnectionFactory");
        provider.GetRequiredService<DeepgramStreamingSink>().Should().NotBeNull();
    }
}
