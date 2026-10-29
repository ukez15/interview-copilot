using FluentAssertions;
using InterviewCopilot.Application.Audio;
using InterviewCopilot.Application.Common.Interfaces;
using InterviewCopilot.Domain.Entities;
using InterviewCopilot.Domain.Enums;
using InterviewCopilot.UnitTests.Services;
using Microsoft.Extensions.DependencyInjection;

namespace InterviewCopilot.UnitTests.Audio;

public sealed class SpeechModelSelectorTests : IDisposable
{
    private readonly InMemoryStore _store = new();
    private readonly InMemoryPreferences _preferences = new();
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Session _session;
    private readonly SpeechModelSelector _selector;
    private readonly ServiceProvider _provider;

    public SpeechModelSelectorTests()
    {
        _session = Session.Create(_owner, "Mock", SessionMode.MockInterview, FixedClock.Default);
        _store.Sessions.Add(_session);
        var services = new ServiceCollection();
        services.AddScoped<ISessionRepository>(_ => _store);
        services.AddScoped<IUserPreferencesRepository>(_ => _preferences);
        _provider = services.BuildServiceProvider();
        _selector = new SpeechModelSelector(_provider.GetRequiredService<IServiceScopeFactory>());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _provider.Dispose();

    private void Choose(string? model)
    {
        var prefs = _preferences.Items.Find(p => p.UserId == _owner) ?? UserPreferences.Create(_owner, FixedClock.Default);
        prefs.SetSpeechModel(model, FixedClock.Default);
        if (!_preferences.Items.Contains(prefs))
        {
            _preferences.Add(prefs);
        }
    }

    [Fact]
    public async Task Should_UseTheDefault_When_TheOwnerNeverChose()
    {
        (await _selector.ForSessionAsync(_session.Id, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Should_UseTheOwnersChoice_AndKeepItForTheSession_UntilForgotten()
    {
        Choose(FakeSpeechModels.Accurate);
        (await _selector.ForSessionAsync(_session.Id, Ct)).Should().Be(FakeSpeechModels.Accurate);

        Choose(FakeSpeechModels.Standard);
        (await _selector.ForSessionAsync(_session.Id, Ct)).Should().Be(FakeSpeechModels.Accurate, "a live session keeps its model");

        _selector.Forget(_session.Id);
        (await _selector.ForSessionAsync(_session.Id, Ct)).Should().Be(FakeSpeechModels.Standard);
    }

    [Fact]
    public async Task Should_UseTheDefault_AndLookAgain_When_TheSessionCantBeRead()
    {
        var unknown = Guid.NewGuid();
        (await _selector.ForSessionAsync(unknown, Ct)).Should().BeNull();

        var late = Session.Create(_owner, "Late", SessionMode.MockInterview, FixedClock.Default);
        Choose(FakeSpeechModels.Accurate);
        _store.Sessions.Add(late);

        (await _selector.ForSessionAsync(late.Id, Ct)).Should().Be(FakeSpeechModels.Accurate);
    }
}
