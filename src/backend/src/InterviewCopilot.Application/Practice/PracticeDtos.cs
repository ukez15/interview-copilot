using InterviewCopilot.Domain.Entities;

namespace InterviewCopilot.Application.Practice;

/// <summary>The next question, plus where its answer starts in the candidate's audio stream (#78).</summary>
/// <param name="Question">The question to show.</param>
/// <param name="AnswerFromMs">Candidate speech that started before this position (ms) isn't part of the answer.</param>
public sealed record PracticeQuestionDto(string Question, long AnswerFromMs = 0);

/// <summary>One piece of a streamed score (#78); at most one of its optional values is set.</summary>
/// <param name="Delta">New feedback text; empty when this part carries something else.</param>
/// <param name="Score">The score, once, as soon as the model has written it.</param>
/// <param name="Restart">The reply couldn't be read and is being asked again: drop what was shown.</param>
/// <param name="Attempt">The last part: the saved attempt.</param>
public sealed record PracticeScorePart(string Delta, int? Score = null, bool Restart = false, PracticeAttemptDto? Attempt = null);

/// <summary>One piece of a streamed question (#78).</summary>
/// <param name="Delta">Text the model just wrote; empty on the last part.</param>
/// <param name="Question">Set only on the last part: the whole question, now open for the answer.</param>
public sealed record PracticeQuestionPart(string Delta, PracticeQuestionDto? Question = null);

public sealed record PracticeAttemptDto(Guid Id, string Question, string AnswerText, int Score, string Feedback, DateTimeOffset CreatedAt)
{
    public static PracticeAttemptDto From(PracticeAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        return new PracticeAttemptDto(attempt.Id, attempt.Question, attempt.AnswerText, attempt.Score, attempt.Feedback, attempt.CreatedAt);
    }
}
