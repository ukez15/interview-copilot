using FluentAssertions;
using InterviewCopilot.Application.Common.Interfaces;
using InterviewCopilot.Application.Practice;
using InterviewCopilot.Domain.Entities;
using InterviewCopilot.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace InterviewCopilot.UnitTests.Practice;

public sealed class PracticeCaptureGateTests : IDisposable
{
    private readonly InMemoryStore _store = new();
    private readonly PracticeAnswerCollector _collector = new();
    private readonly ServiceProvider _provider;
    private readonly PracticeCaptureGate _gate;

    public PracticeCaptureGateTests()
    {
        var services = new ServiceCollection();
        services.AddScoped<ISessionRepository>(_ => _store);
        _provider = services.BuildServiceProvider();
        _gate = new PracticeCaptureGate(_provider.GetRequiredService<IServiceScopeFactory>(), _collector);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _provider.Dispose();

    private Session Add(SessionMode mode)
    {
        var session = Session.Create(Guid.NewGuid(), "S", mode, FixedClock.Default);
        _store.Sessions.Add(session);
        return session;
    }

    [Fact]
    public async Task Should_DropTheCandidate_InAMockInterview_While_NoQuestionIsOpen()
    {
        var session = Add(SessionMode.MockInterview);

        (await _gate.AcceptsAsync(session.Id, Speaker.Candidate, Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Should_TranscribeTheCandidate_While_AQuestionIsOpen_AndStopAfterIt()
    {
        var session = Add(SessionMode.MockInterview);

        _collector.Open(session.Id, "Q");
        (await _gate.AcceptsAsync(session.Id, Speaker.Candidate, Ct)).Should().BeTrue();

        _collector.Take(session.Id);
        (await _gate.AcceptsAsync(session.Id, Speaker.Candidate, Ct)).Should().BeFalse();
    }

    [Theory]
    [InlineData(SessionMode.LiveInterview)]
    [InlineData(SessionMode.Conversation)]
    public async Task Should_AlwaysTranscribe_OtherModes(SessionMode mode)
    {
        var session = Add(mode);

        (await _gate.AcceptsAsync(session.Id, Speaker.Candidate, Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task Should_AlwaysTranscribeTheInterviewer()
    {
        var session = Add(SessionMode.MockInterview);

        (await _gate.AcceptsAsync(session.Id, Speaker.Interviewer, Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task Should_Transcribe_AndLookAgain_When_TheSessionCantBeRead()
    {
        var session = Session.Create(Guid.NewGuid(), "S", SessionMode.MockInterview, FixedClock.Default);
        (await _gate.AcceptsAsync(session.Id, Speaker.Candidate, Ct)).Should().BeTrue();

        _store.Sessions.Add(session);

        (await _gate.AcceptsAsync(session.Id, Speaker.Candidate, Ct)).Should().BeFalse();
    }
}
