using System.Text;
using InterviewCopilot.Application.Common.Exceptions;
using InterviewCopilot.Application.Common.Interfaces;
using InterviewCopilot.Application.Providers;
using InterviewCopilot.Application.Sessions;
using InterviewCopilot.Domain.Entities;
using InterviewCopilot.Domain.Enums;
using InterviewCopilot.Domain.Exceptions;
using Microsoft.Extensions.Options;

namespace InterviewCopilot.Application.Practice;

public sealed record PracticeQuestionDto(string Question);

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
    TimeProvider clock)
{
    /// <summary>Extra tries when the model's score reply can't be parsed.</summary>
    public const int ScoreRetries = 1;

    public const string NoAnswerMessage = "No answer heard.";

    public const string UnreadableFeedbackMessage = "The model's feedback couldn't be read. Try again.";

    public const string NoQuestionMessage = "The model didn't return a question. Try again.";

    public async Task<PracticeQuestionDto> NextAsync(Guid ownerId, Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await LoadLiveMockAsync(ownerId, sessionId, cancellationToken);
        var asked = await attempts.ListAsync(sessionId, cancellationToken);
        var reply = await CompleteAsync(
            session,
            PracticePromptBuilder.NextQuestion(session, [.. asked.Select(a => a.Question)]),
            cancellationToken);

        var question = reply.Trim();
        if (question.Length == 0)
        {
            throw new LlmUnavailableException(NoQuestionMessage);
        }

        collector.Open(sessionId, question);
        return new PracticeQuestionDto(question);
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

    private async Task<string> CompleteAsync(Session session, IReadOnlyList<LlmMessage> messages, CancellationToken cancellationToken)
    {
        var options = llmOptions.Value;
        var owner = await preferences.GetAsync(session.OwnerId, cancellationToken);

        // Ollama only in this wave: the owner's model applies only when their chosen provider is Ollama (or unset).
        var ownerUsesOllama = string.IsNullOrWhiteSpace(owner?.LlmProvider)
            || string.Equals(owner.LlmProvider, ProviderNames.Ollama, StringComparison.OrdinalIgnoreCase);
        var model = ownerUsesOllama && !string.IsNullOrWhiteSpace(owner?.LlmModel) ? owner.LlmModel : options.Model;

        var llm = llmProviders.Resolve(ProviderNames.Ollama);
        var reply = new StringBuilder();
        await foreach (var token in llm.StreamAsync(new LlmRequest(model, messages, options.Temperature), cancellationToken))
        {
            reply.Append(token);
        }

        return reply.ToString();
    }
}
