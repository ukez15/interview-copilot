using FluentAssertions;
using InterviewCopilot.Application.Audio;
using InterviewCopilot.Application.Common.Interfaces;
using InterviewCopilot.Application.Providers;
using InterviewCopilot.Domain.Entities;
using InterviewCopilot.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace InterviewCopilot.UnitTests.Audio;

public sealed class SpeechToTextRouterTests : IDisposable
{
    private readonly InMemoryStore _store = new();
    private readonly InMemoryPreferences _preferences = new();
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Session _session;
    private readonly TranscriptionOptions _options = new();
    private ServiceProvider? _provider;

    public SpeechToTextRouterTests()
    {
        _session = Session.Create(_owner, "Live", SessionMode.LiveInterview, FixedClock.Default);
        _store.Sessions.Add(_session);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _provider?.Dispose();

    private SpeechToTextRouter Router()
    {
        var services = new ServiceCollection();
        services.AddScoped<ISessionRepository>(_ => _store);
        services.AddScoped<IUserPreferencesRepository>(_ => _preferences);
        _provider = services.BuildServiceProvider();
        return new SpeechToTextRouter(_provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(_options));
    }

    private void Choose(string? provider)
    {
        var prefs = UserPreferences.Create(_owner, FixedClock.Default);
        prefs.SetSpeechToTextProvider(provider, FixedClock.Default);
        _preferences.Add(prefs);
    }

    [Fact]
    public async Task Should_UseWhisper_When_NothingIsConfigured()
    {
        var router = Router();

        (await router.UsesDeepgramAsync(_session.Id, Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Should_UseDeepgram_When_OwnerChoseItAndConfigEnablesIt()
    {
        _options.Providers.Deepgram.Enabled = true;
        Choose(ProviderNames.Deepgram);

        (await Router().UsesDeepgramAsync(_session.Id, Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task Should_UseDeepgram_When_ConfiguredDefaultIsDeepgramAndOwnerHasNoChoice()
    {
        _options.Providers.Deepgram.Enabled = true;
        _options.Provider = "Deepgram";

        (await Router().UsesDeepgramAsync(_session.Id, Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task Should_UseWhisper_When_OwnerChoseWhisperOverADeepgramDefault()
    {
        _options.Providers.Deepgram.Enabled = true;
        _options.Provider = ProviderNames.Deepgram;
        Choose(ProviderNames.Whisper);

        (await Router().UsesDeepgramAsync(_session.Id, Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Should_UseWhisper_When_DeepgramIsChosenButDisabledInConfig()
    {
        Choose(ProviderNames.Deepgram);
        _options.Provider = ProviderNames.Deepgram;
        _preferences.Failure = new InvalidOperationException("must not be read while Deepgram is disabled");

        (await Router().UsesDeepgramAsync(_session.Id, Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Should_KeepFirstDecision_UntilForgotten()
    {
        _options.Providers.Deepgram.Enabled = true;
        Choose(ProviderNames.Deepgram);
        var router = Router();
        (await router.UsesDeepgramAsync(_session.Id, Ct)).Should().BeTrue();

        Choose(ProviderNames.Whisper);

        (await router.UsesDeepgramAsync(_session.Id, Ct)).Should().BeTrue();
        router.Forget(_session.Id);
        (await router.UsesDeepgramAsync(_session.Id, Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Should_UseWhisperAndDecideAgain_When_SessionIsUnknownAtFirst()
    {
        _options.Providers.Deepgram.Enabled = true;
        Choose(ProviderNames.Deepgram);
        var router = Router();
        _store.Sessions.Clear();

        (await router.UsesDeepgramAsync(_session.Id, Ct)).Should().BeFalse();

        _store.Sessions.Add(_session);
        (await router.UsesDeepgramAsync(_session.Id, Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task Should_UseWhisperAndDecideAgain_When_PreferencesCannotBeRead()
    {
        _options.Providers.Deepgram.Enabled = true;
        Choose(ProviderNames.Deepgram);
        var router = Router();
        _preferences.Failure = new InvalidOperationException("database busy");

        (await router.UsesDeepgramAsync(_session.Id, Ct)).Should().BeFalse();

        _preferences.Failure = null;
        (await router.UsesDeepgramAsync(_session.Id, Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task Should_DecideOncePerSession_When_FramesArriveConcurrently()
    {
        _options.Providers.Deepgram.Enabled = true;
        Choose(ProviderNames.Deepgram);
        var router = Router();

        var answers = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => router.UsesDeepgramAsync(_session.Id, Ct).AsTask()));

        answers.Should().OnlyContain(a => a);
    }

    [Fact]
    public void Should_ThrowArgumentNull_When_ConstructorArgumentsMissing()
    {
        var noScopes = () => new SpeechToTextRouter(null!, Options.Create(_options));
        var noOptions = () => new SpeechToTextRouter(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), null!);

        noScopes.Should().Throw<ArgumentNullException>();
        noOptions.Should().Throw<ArgumentNullException>();
    }
}
