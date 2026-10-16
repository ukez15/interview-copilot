using System.Text.Json;
using FluentAssertions;
using InterviewCopilot.Application.Common.Exceptions;
using InterviewCopilot.Application.Events;
using InterviewCopilot.Application.Sessions;
using InterviewCopilot.Domain.Enums;

namespace InterviewCopilot.UnitTests.Services;

public sealed class AutoAnswerToggleTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private readonly InMemoryStore _store = new();
    private readonly List<(string Type, string Json)> _published = [];
    private readonly SessionService _sessions;

    public AutoAnswerToggleTests() => _sessions = new SessionService(_store, _store, new FixedClock(), new Capture(_published));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<SessionDto> CreateAsync() => _sessions.CreateAsync(
        new CreateSessionCommand(Owner, "Acme", SessionMode.LiveInterview, AnswerLength.Short, AnswerFramework.None, null, null),
        Ct);

    [Fact]
    public async Task Should_SaveAndPublishState_When_Toggled()
    {
        var created = await CreateAsync();
        created.AutoAnswer.Should().BeTrue();

        var paused = await _sessions.SetAutoAnswerAsync(Owner, created.Id, false, Ct);

        paused.AutoAnswer.Should().BeFalse();
        _store.Sessions.Single().AutoAnswer.Should().BeFalse();
        _published.Should().ContainSingle().Which.Should().Be((SessionEventTypes.AutoAnswerState, "{\"enabled\":false}"));
    }

    [Fact]
    public async Task Should_Throw_When_NotOwner()
    {
        var created = await CreateAsync();

        var act = () => _sessions.SetAutoAnswerAsync(Guid.NewGuid(), created.Id, false, Ct);

        await act.Should().ThrowAsync<NotFoundException>();
        _published.Should().BeEmpty();
    }

    [Fact]
    public async Task Should_StillSave_When_NoPublisherRegistered()
    {
        var sessions = new SessionService(_store, _store, new FixedClock());
        var created = await CreateAsync();

        var result = await sessions.SetAutoAnswerAsync(Owner, created.Id, false, Ct);

        result.AutoAnswer.Should().BeFalse();
    }

    private sealed class Capture(List<(string Type, string Json)> sink) : ISessionEventPublisher
    {
        public void Publish(Guid sessionId, string type, object data) => sink.Add((type, JsonSerializer.Serialize(data)));
    }
}
