namespace InterviewCopilot.Application.Events;

/// <summary>Event names on the per-session events channel (docs/atlas-gotcha/wave-1.md, #23).</summary>
public static class SessionEventTypes
{
    public const string TranscriptSegment = "transcript.segment";
    public const string QuestionDetected = "question.detected";
    public const string AnswerDelta = "answer.delta";
    public const string AnswerDone = "answer.done";
    public const string Error = "error";
    public const string AutoAnswerState = "autoanswer.state";

    /// <summary>#78: the candidate has been quiet for 6 s after answering, so the overlay can score.</summary>
    public const string PracticeSilence = "practice.silence";
}

/// <summary>One event as sent to clients: <c>{"id":n,"type":"…","data":{…}}</c>. Ids increase per session from 1.</summary>
public sealed record SessionEvent(long Id, string Type, object Data);

/// <summary>Publishes events to everyone watching a session (overlay, main window, extension).</summary>
public interface ISessionEventPublisher
{
    /// <summary>Sends an event to the session's subscribers.</summary>
    /// <param name="sessionId">The session the event belongs to.</param>
    /// <param name="type">One of <see cref="SessionEventTypes"/>.</param>
    /// <param name="data">A JSON-serializable payload. Never include API keys.</param>
    void Publish(Guid sessionId, string type, object data);
}

/// <summary>Default until the events hub (#23) is registered: drops events.</summary>
internal sealed class NullSessionEventPublisher : ISessionEventPublisher
{
    public void Publish(Guid sessionId, string type, object data)
    {
        // Intentionally empty: no subscribers exist without the events hub.
    }
}
