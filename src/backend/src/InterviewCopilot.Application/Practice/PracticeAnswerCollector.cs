using InterviewCopilot.Application.Audio;
using InterviewCopilot.Domain.Entities;
using InterviewCopilot.Domain.Enums;

namespace InterviewCopilot.Application.Practice;

/// <summary>The question a session is waiting on and what the candidate has said since it was asked.</summary>
/// <param name="Question">The question that was asked.</param>
/// <param name="Answer">The candidate's segments, joined with spaces.</param>
/// <param name="FromMs">Where the answer starts in the candidate's audio stream; earlier speech doesn't count (#78).</param>
public sealed record CollectedAnswer(string Question, string Answer, long FromMs = 0);

/// <summary>
/// Collects the candidate's spoken answer in mock interview mode (#26). Holds, per session, the open question and
/// the Candidate segment texts spoken after it was shown; interviewer segments, and speech that started before
/// the question but was transcribed late, are ignored (#78). Kept in memory only.
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
            if (_open.TryGetValue(segment.SessionId, out var open) && segment.StartMs >= open.FromMs)
            {
                open.Segments.Add(segment.Text.Trim());
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>True while the session has a question waiting for its answer.</summary>
    /// <param name="sessionId">The session being practised.</param>
    /// <returns>True when a question is open.</returns>
    public bool IsOpen(Guid sessionId)
    {
        lock (_gate)
        {
            return _open.ContainsKey(sessionId);
        }
    }

    /// <summary>
    /// True once per open question, and only after the candidate said something: lets the silence after an answer
    /// be announced a single time (#78).
    /// </summary>
    /// <param name="sessionId">The session being practised.</param>
    /// <returns>True when the caller should announce the silence.</returns>
    public bool TryClaimSilence(Guid sessionId)
    {
        lock (_gate)
        {
            if (!_open.TryGetValue(sessionId, out var open) || open.Segments.Count == 0 || open.SilenceClaimed)
            {
                return false;
            }

            open.SilenceClaimed = true;
            return true;
        }
    }

    /// <summary>Opens a question for the session, replacing any unanswered one and its collected text.</summary>
    /// <param name="sessionId">The session being practised.</param>
    /// <param name="question">The question that was just asked.</param>
    /// <param name="fromMs">The candidate's stream position when the question was shown; speech that started earlier is ignored.</param>
    public void Open(Guid sessionId, string question, long fromMs = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        lock (_gate)
        {
            _open[sessionId] = new OpenQuestion(question, fromMs);
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
                ? new CollectedAnswer(open.Question, string.Join(' ', open.Segments), open.FromMs)
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

            var open = new OpenQuestion(collected.Question, collected.FromMs);
            if (collected.Answer.Length > 0)
            {
                open.Segments.Add(collected.Answer);
            }

            _open[sessionId] = open;
        }
    }

    private sealed class OpenQuestion(string question, long fromMs)
    {
        public string Question { get; } = question;

        public long FromMs { get; } = fromMs;

        public List<string> Segments { get; } = [];

        public bool SilenceClaimed { get; set; }
    }
}
