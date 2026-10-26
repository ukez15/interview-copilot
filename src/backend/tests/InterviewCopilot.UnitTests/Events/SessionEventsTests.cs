using FluentAssertions;
using InterviewCopilot.Application;
using InterviewCopilot.Application.Events;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace InterviewCopilot.UnitTests.Events;

public class SessionEventsTests
{
    [Fact]
    public void Should_RegisterNoOpPublisher_ByDefault()
    {
        using var provider = new ServiceCollection()
            .AddApplication(new ConfigurationBuilder().Build())
            .BuildServiceProvider();

        var publisher = provider.GetRequiredService<ISessionEventPublisher>();
        publisher.Publish(Guid.NewGuid(), SessionEventTypes.AnswerDelta, new { token = "x" });

        publisher.Should().BeOfType<NullSessionEventPublisher>();
    }

    [Fact]
    public void Should_UseAgreedEventNames()
    {
        new[] { SessionEventTypes.TranscriptSegment, SessionEventTypes.QuestionDetected, SessionEventTypes.AnswerDelta, SessionEventTypes.AnswerDone, SessionEventTypes.Error }
            .Should().Equal("transcript.segment", "question.detected", "answer.delta", "answer.done", "error");
        new SessionEvent(1, SessionEventTypes.Error, new { message = "m" }).Id.Should().Be(1);
    }
}
