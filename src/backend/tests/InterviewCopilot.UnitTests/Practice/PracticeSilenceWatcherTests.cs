using FluentAssertions;
using InterviewCopilot.Application.Audio;
using InterviewCopilot.Application.Events;
using InterviewCopilot.Application.Practice;
using InterviewCopilot.Domain.Entities;
using InterviewCopilot.Domain.Enums;

namespace InterviewCopilot.UnitTests.Practice;

public class PracticeSilenceWatcherTests
{
    private readonly Guid _session = Guid.NewGuid();
    private readonly PracticeAnswerCollector _collector = new();
    private readonly Publisher _events = new();
    private readonly PracticeSilenceWatcher _watcher;

    public PracticeSilenceWatcherTests() => _watcher = new PracticeSilenceWatcher(_collector, _events);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task AnswerAsync(string text) =>
        await _collector.OnSegmentAsync(TranscriptSegment.Create(_session, Speaker.Candidate, 1_000, 2_000, text), Ct);

    private ValueTask FrameAsync(long positionMs, long lastVoiceEndMs, bool pending = false, Speaker speaker = Speaker.Candidate) =>
        _watcher.OnFrameHandledAsync(new AudioProgress(_session, speaker, positionMs, lastVoiceEndMs, pending), Ct);

    [Fact]
    public async Task Should_SignalOnce_After6sWithoutVoice_OnceSomethingWasSaid()
    {
        _collector.Open(_session, "Why us?");
        await AnswerAsync("Because of the mission.");

        await FrameAsync(2_000 + PracticeSilenceWatcher.SilenceMs - 100, 2_000);
        _events.Count.Should().Be(0);
        await FrameAsync(2_000 + PracticeSilenceWatcher.SilenceMs, 2_000);
        await FrameAsync(2_000 + PracticeSilenceWatcher.SilenceMs + 100, 2_000);

        _events.Count.Should().Be(1);
        _events.Last.Should().Be((_session, SessionEventTypes.PracticeSilence));
    }

    [Fact]
    public async Task Should_NotSignal_BeforeTheCandidateSaysAnything()
    {
        _collector.Open(_session, "Why us?");

        await FrameAsync(60_000, 0);

        _events.Count.Should().Be(0);
    }

    [Fact]
    public async Task Should_NotSignal_WhileSpeechIsStillBeingCutOrTranscribed()
    {
        _collector.Open(_session, "Why us?");
        await AnswerAsync("First point.");

        await FrameAsync(20_000, 2_000, pending: true);

        _events.Count.Should().Be(0);
    }

    [Fact]
    public async Task Should_IgnoreTheInterviewer_AndClosedQuestions()
    {
        await FrameAsync(20_000, 0);
        _collector.Open(_session, "Why us?");
        await AnswerAsync("words");

        await FrameAsync(20_000, 0, speaker: Speaker.Interviewer);

        _events.Count.Should().Be(0);
    }

    [Fact]
    public async Task Should_SignalAgain_ForTheNextQuestion()
    {
        _collector.Open(_session, "First?");
        await AnswerAsync("one");
        await FrameAsync(10_000, 2_000);
        _collector.Open(_session, "Second?");
        await AnswerAsync("two");

        await FrameAsync(10_000, 2_000);

        _events.Count.Should().Be(2);
    }

    private sealed class Publisher : ISessionEventPublisher
    {
        private readonly List<(Guid SessionId, string Type)> _published = [];

        public int Count => _published.Count;

        public (Guid SessionId, string Type) Last => _published[^1];

        public void Publish(Guid sessionId, string type, object data) => _published.Add((sessionId, type));
    }
}
