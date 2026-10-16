using FluentAssertions;
using InterviewCopilot.Application.Events;

namespace InterviewCopilot.UnitTests.Events;

public class SessionEventHubTests
{
    private static List<SessionEvent> Drain(SessionEventSubscription subscription)
    {
        var events = new List<SessionEvent>();
        while (subscription.Reader.TryRead(out var item))
        {
            events.Add(item);
        }

        return events;
    }

    [Fact]
    public void Should_NumberEventsPerSession_StartingAtOne()
    {
        var hub = new SessionEventHub();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        using var subA = hub.Subscribe(a);
        using var subB = hub.Subscribe(b);

        hub.Publish(a, SessionEventTypes.AnswerDelta, new { token = "x" });
        hub.Publish(a, SessionEventTypes.AnswerDone, new { version = 1 });
        hub.Publish(b, SessionEventTypes.Error, new { message = "m" });

        Drain(subA).Select(e => e.Id).Should().Equal(1, 2);
        Drain(subB).Select(e => e.Id).Should().Equal(1);
    }

    [Fact]
    public void Should_FanOutToEverySubscriber_OfTheSession()
    {
        var hub = new SessionEventHub();
        var sessionId = Guid.NewGuid();
        using var first = hub.Subscribe(sessionId);
        using var second = hub.Subscribe(sessionId);
        using var other = hub.Subscribe(Guid.NewGuid());

        hub.Publish(sessionId, SessionEventTypes.AnswerDelta, new { token = "a" });
        hub.Publish(sessionId, SessionEventTypes.AnswerDelta, new { token = "b" });

        Drain(first).Select(e => e.Id).Should().Equal(1, 2);
        Drain(second).Select(e => e.Id).Should().Equal(1, 2);
        Drain(other).Should().BeEmpty();
    }

    [Fact]
    public void Should_DropOnlyTheSlowSubscribersOldestEvents()
    {
        var hub = new SessionEventHub(bufferSize: 3);
        var sessionId = Guid.NewGuid();
        using var slow = hub.Subscribe(sessionId);
        using var fast = hub.Subscribe(sessionId);

        for (var i = 1; i <= 5; i++)
        {
            hub.Publish(sessionId, SessionEventTypes.AnswerDelta, new { n = i });
            if (i < 5)
            {
                Drain(fast).Should().ContainSingle();
            }
        }

        Drain(slow).Select(e => e.Id).Should().Equal(3, 4, 5);
        Drain(fast).Select(e => e.Id).Should().Equal(5);
    }

    [Fact]
    public void Should_StopDelivering_AfterUnsubscribe()
    {
        var hub = new SessionEventHub();
        var sessionId = Guid.NewGuid();
        var subscription = hub.Subscribe(sessionId);
        using var stays = hub.Subscribe(sessionId);

        subscription.Dispose();
        subscription.Dispose(); // idempotent
        hub.Publish(sessionId, SessionEventTypes.AnswerDelta, new { token = "x" });

        Drain(subscription).Should().BeEmpty();
        Drain(stays).Should().ContainSingle();
        hub.SubscriberCount(sessionId).Should().Be(1);
    }

    [Fact]
    public void Should_KeepIdsIncreasing_WhenNobodyIsSubscribed()
    {
        var hub = new SessionEventHub();
        var sessionId = Guid.NewGuid();

        hub.Publish(sessionId, SessionEventTypes.AnswerDelta, new { token = "x" });
        using var late = hub.Subscribe(sessionId);
        hub.Publish(sessionId, SessionEventTypes.AnswerDelta, new { token = "y" });

        Drain(late).Select(e => e.Id).Should().Equal(2);
        hub.SubscriberCount(Guid.NewGuid()).Should().Be(0);
    }

    [Fact]
    public void Should_Throw_When_ArgumentsInvalid()
    {
        var hub = new SessionEventHub();

        var publishNull = () => hub.Publish(Guid.NewGuid(), null!, new { });
        var dataNull = () => hub.Publish(Guid.NewGuid(), SessionEventTypes.Error, null!);
        var badSize = () => new SessionEventHub(0);

        publishNull.Should().Throw<ArgumentNullException>();
        dataNull.Should().Throw<ArgumentNullException>();
        badSize.Should().Throw<ArgumentOutOfRangeException>();
    }
}
