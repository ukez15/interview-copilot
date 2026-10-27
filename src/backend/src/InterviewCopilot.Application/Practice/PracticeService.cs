using System.Runtime.CompilerServices;
using System.Text;
using InterviewCopilot.Application.Audio;
using InterviewCopilot.Application.Common.Exceptions;
using InterviewCopilot.Application.Common.Interfaces;
using InterviewCopilot.Application.Providers;
using InterviewCopilot.Application.Sessions;
using InterviewCopilot.Domain.Entities;
using InterviewCopilot.Domain.Enums;
using InterviewCopilot.Domain.Exceptions;
using Microsoft.Extensions.Options;

namespace InterviewCopilot.Application.Practice;

/// <summary>The next question, plus where its answer starts in the candidate's audio stream (#78).</summary>
/// <param name="Question">The question to show.</param>
/// <param name="AnswerFromMs">Candidate speech that started before this position (ms) isn't part of the answer.</param>
public sealed record PracticeQuestionDto(string Question, long AnswerFromMs = 0);

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

/// <summary>Mock interview mode: ask a question, score the spoken answer, keep the attempt (FR-11, #26).</summary>
public sealed class PracticeService(
    SessionService sessions,
    IPracticeAttemptRepository attempts,
    IUnitOfWork unitOfWork,
    IUserPreferencesRepository preferences,
    ILlmProviderResolver llmProviders,
    IOptions<LlmOptions> llmOptions,
    PracticeAnswerCollector collector,
    TimeProvider clock,
    IAudioStreamClock audioClock)
{
    /// <summary>Extra tries when the model's score reply can't be parsed.</summary>
    public const int ScoreRetries = 1;

    public const string NoAnswerMessage = "No answer heard.";

    public const string UnreadableFeedbackMessage = "The model's feedback couldn't be read. Try again.";

    public const string NoQuestionMessage = "The model didn't return a question. Try again.";

    /// <summary>
    /// Checks the session, then streams the next question as the model writes it (#78). The last part carries the
    /// whole question, which is opened for the answer only then; a model failure surfaces while enumerating.
    /// </summary>
    public async Task<IAsyncEnumerable<PracticeQuestionPart>> NextAsync(Guid ownerId, Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await LoadLiveMockAsync(ownerId, sessionId, cancellationToken);
        var asked = await attempts.ListAsync(sessionId, cancellationToken);
        collector.Take(sessionId); // a skipped question closes, so the mic rests while the next one is written
        var request = await RequestAsync(session, PracticePromptBuilder.NextQuestion(session, [.. asked.Select(a => a.Question)]), cancellationToken);
        return StreamQuestionAsync(sessionId, request, cancellationToken);
    }

    public async Task<PracticeAttemptDto> FinishAsync(Guid ownerId, Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await LoadLiveMockAsync(ownerId, sessionId, cancellationToken);
        var collected = collector.Take(sessionId);
        if (collected is null || string.IsNullOrWhiteSpace(collected.Answer))
        {
            if (collected is not null)
            {
                collector.Restore(sessionId, collected); // keep the question open so the candidate can still answer
            }

            throw new DomainException(NoAnswerMessage);
        }

        PracticeFeedback feedback;
        try
        {
            feedback = await ScoreAsync(session, collected, cancellationToken);
        }
        catch (Exception ex) when (ex is LlmUnavailableException or OperationCanceledException)
        {
            collector.Restore(sessionId, collected);
            throw;
        }

        var attempt = PracticeAttempt.Create(
            sessionId, collected.Question, collected.Answer, feedback.Score, feedback.Feedback, clock.GetUtcNow());
        attempts.Add(attempt);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return PracticeAttemptDto.From(attempt);
    }

    public async Task<IReadOnlyList<PracticeAttemptDto>> ListAsync(Guid ownerId, Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await sessions.LoadOwnedAsync(ownerId, sessionId, cancellationToken);
        RequireMock(session);
        var saved = await attempts.ListAsync(sessionId, cancellationToken);
        return [.. saved.OrderBy(a => a.CreatedAt).Select(PracticeAttemptDto.From)];
    }

    private static void RequireMock(Session session)
    {
        if (session.Mode != SessionMode.MockInterview)
        {
            throw new DomainException("Practice is only available in mock interview sessions.");
        }
    }

    private async Task<Session> LoadLiveMockAsync(Guid ownerId, Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await sessions.LoadOwnedAsync(ownerId, sessionId, cancellationToken);
        RequireMock(session);
        if (session.Status != SessionStatus.Live)
        {
            throw new ConflictException("Practice needs a live session.");
        }

        return session;
    }

    private async Task<PracticeFeedback> ScoreAsync(Session session, CollectedAnswer collected, CancellationToken cancellationToken)
    {
        var messages = PracticePromptBuilder.ScoreAnswer(session, collected.Question, collected.Answer);
        for (var attempt = 0; attempt <= ScoreRetries; attempt++)
        {
            var parsed = PracticePromptBuilder.ParseFeedback(await CompleteAsync(session, messages, cancellationToken));
            if (parsed is not null)
            {
                return parsed;
            }
        }

        throw new LlmUnavailableException(UnreadableFeedbackMessage);
    }

    private async IAsyncEnumerable<PracticeQuestionPart> StreamQuestionAsync(
        Guid sessionId,
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var reply = new StringBuilder();
        await foreach (var token in llmProviders.Resolve(ProviderNames.Ollama).StreamAsync(request, cancellationToken))
        {
            reply.Append(token);
            yield return new PracticeQuestionPart(token);
        }

        var question = reply.ToString().Trim();
        if (question.Length == 0)
        {
            throw new LlmUnavailableException(NoQuestionMessage);
        }

        // The answer starts now, as the question is shown: speech from while it was being written doesn't count.
        var answerFromMs = audioClock.PositionMs(sessionId, Speaker.Candidate);
        collector.Open(sessionId, question, answerFromMs);
        yield return new PracticeQuestionPart(string.Empty, new PracticeQuestionDto(question, answerFromMs));
    }

    private async Task<string> CompleteAsync(Session session, IReadOnlyList<LlmMessage> messages, CancellationToken cancellationToken)
    {
        var reply = new StringBuilder();
        var request = await RequestAsync(session, messages, cancellationToken);
        await foreach (var token in llmProviders.Resolve(ProviderNames.Ollama).StreamAsync(request, cancellationToken))
        {
            reply.Append(token);
        }

        return reply.ToString();
    }

    private async Task<LlmRequest> RequestAsync(Session session, IReadOnlyList<LlmMessage> messages, CancellationToken cancellationToken)
    {
        var options = llmOptions.Value;
        var owner = await preferences.GetAsync(session.OwnerId, cancellationToken);

        // Ollama only in this wave: the owner's model applies only when their chosen provider is Ollama (or unset).
        var ownerUsesOllama = string.IsNullOrWhiteSpace(owner?.LlmProvider)
            || string.Equals(owner.LlmProvider, ProviderNames.Ollama, StringComparison.OrdinalIgnoreCase);
        var model = ownerUsesOllama && !string.IsNullOrWhiteSpace(owner?.LlmModel) ? owner.LlmModel : options.Model;

        return new LlmRequest(model, messages, options.Temperature);
    }
}
