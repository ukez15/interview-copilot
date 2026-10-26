using FluentAssertions;
using InterviewCopilot.Application.Practice;
using InterviewCopilot.Domain.Entities;
using InterviewCopilot.Domain.Enums;

namespace InterviewCopilot.UnitTests.Practice;

public class PracticeAnswerCollectorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TranscriptSegment Segment(Guid sessionId, Speaker speaker, string text) =>
        TranscriptSegment.Create(sessionId, speaker, 0, 1_000, text);

    [Fact]
    public async Task Should_CollectCandidateSegmentsAfterQuestionOpens_PerSession()
    {
        var collector = new PracticeAnswerCollector();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await collector.OnSegmentAsync(Segment(a, Speaker.Candidate, "before the question"), Ct);
        collector.Open(a, "Q-A");
        collector.Open(b, "Q-B");

        await collector.OnSegmentAsync(Segment(a, Speaker.Candidate, "first part"), Ct);
        await collector.OnSegmentAsync(Segment(b, Speaker.Candidate, "other session"), Ct);
        await collector.OnSegmentAsync(Segment(a, Speaker.Candidate, " second part "), Ct);

        collector.Take(a).Should().Be(new CollectedAnswer("Q-A", "first part second part"));
        collector.Take(b).Should().Be(new CollectedAnswer("Q-B", "other session"));
    }

    [Fact]
    public async Task Should_IgnoreInterviewerSegments()
    {
        var collector = new PracticeAnswerCollector();
        var id = Guid.NewGuid();
        collector.Open(id, "Q");

        await collector.OnSegmentAsync(Segment(id, Speaker.Interviewer, "their words"), Ct);

        collector.Take(id)!.Answer.Should().BeEmpty();
    }

    [Fact]
    public async Task Should_ReplaceUnansweredQuestion_AndClearOnTake()
    {
        var collector = new PracticeAnswerCollector();
        var id = Guid.NewGuid();
        collector.Open(id, "old");
        await collector.OnSegmentAsync(Segment(id, Speaker.Candidate, "stale"), Ct);

        collector.Open(id, "new");

        collector.Take(id).Should().Be(new CollectedAnswer("new", string.Empty));
        collector.Take(id).Should().BeNull();
    }

    [Fact]
    public async Task Should_NotCollect_When_NoQuestionIsOpen()
    {
        var collector = new PracticeAnswerCollector();
        var id = Guid.NewGuid();

        await collector.OnSegmentAsync(Segment(id, Speaker.Candidate, "hello"), Ct);

        collector.Take(id).Should().BeNull();
    }

    [Fact]
    public void Should_RestoreTakenAnswer_UnlessNewerQuestionOpened()
    {
        var collector = new PracticeAnswerCollector();
        var id = Guid.NewGuid();
        collector.Restore(id, new CollectedAnswer("Q", "answer"));
        collector.Restore(id, new CollectedAnswer("other", "ignored"));

        collector.Take(id).Should().Be(new CollectedAnswer("Q", "answer"));
    }

    [Fact]
    public void Should_RejectBlankQuestion()
    {
        var act = () => new PracticeAnswerCollector().Open(Guid.NewGuid(), " ");

        act.Should().Throw<ArgumentException>();
    }
}
