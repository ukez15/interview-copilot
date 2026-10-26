using InterviewCopilot.Application.Audio;
using InterviewCopilot.Domain.Entities;
using InterviewCopilot.Domain.Enums;

namespace InterviewCopilot.Application.Practice;

/// <summary>The question a session is waiting on and what the candidate has said since it was asked.</summary>
/// <param name="Question">The question that was asked.</param>
/// <param name="Answer">The candidate's segments, joined with spaces.</param>
public sealed record CollectedAnswer(string Question, string Answer);

/// <summary>
/// Collects the candidate's spoken answer in mock interview mode (#26). Holds, per session, the open question and
/// the Candidate segment texts that arrived after it; interviewer segments are ignored. Kept in memory only.
/// </summary>
public sealed class PracticeAnswerCollector : ITranscriptSegmentListener
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, OpenQuestion> _open = [];

    /// <inheritdoc />
    public ValueTask OnSegmentAsync(TranscriptSegment segment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (segment.Speaker != Speaker.Candidate || string.IsNullOrWhiteSpace(segment.Text))
        {
            return ValueTask.CompletedTask;
        }

        lock (_gate)
        {
            if (_open.TryGetValue(segment.SessionId, out var open))
            {
                open.Segments.Add(segment.Text.Trim());
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Opens a question for the session, replacing any unanswered one and its collected text.</summary>
    /// <param name="sessionId">The session being practised.</param>
    /// <param name="question">The question that was just asked.</param>
    public void Open(Guid sessionId, string question)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        lock (_gate)
        {
            _open[sessionId] = new OpenQuestion(question);
        }
    }

    /// <summary>Returns the open question with its collected answer and closes it; null when none is open.</summary>
    /// <param name="sessionId">The session being practised.</param>
    /// <returns>The question and the answer so far, or null.</returns>
    public CollectedAnswer? Take(Guid sessionId)
    {
        lock (_gate)
        {
            return _open.Remove(sessionId, out var open)
                ? new CollectedAnswer(open.Question, string.Join(' ', open.Segments))
                : null;
        }
    }

    /// <summary>Puts a taken answer back (unless a newer question was opened) so a failed attempt can be retried.</summary>
    /// <param name="sessionId">The session being practised.</param>
    /// <param name="collected">What <see cref="Take"/> returned.</param>
    public void Restore(Guid sessionId, CollectedAnswer collected)
    {
        ArgumentNullException.ThrowIfNull(collected);
        lock (_gate)
        {
            if (_open.ContainsKey(sessionId))
            {
                return;
            }

            var open = new OpenQuestion(collected.Question);
            if (collected.Answer.Length > 0)
            {
                open.Segments.Add(collected.Answer);
            }

            _open[sessionId] = open;
        }
    }

    private sealed class OpenQuestion(string question)
    {
        public string Question { get; } = question;

        public List<string> Segments { get; } = [];
    }
}
