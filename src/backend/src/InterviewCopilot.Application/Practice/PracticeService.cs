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
    /// <summary>Practice runs on the local model only in this wave; it also sets the question's document budget.</summary>
    private const string PracticeProvider = ProviderNames.Ollama;

    /// <summary>Extra tries when the model's score reply can't be parsed.</summary>
    public const int ScoreRetries = 1;

    /// <summary>How long "Done answering" waits for the candidate's last words to be transcribed (#78).</summary>
    public const int LastWordsWaitMs = 5_000;

    /// <summary>How often that wait checks whether transcription has caught up.</summary>
    public const int LastWordsPollMs = 100;

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
        var request = await RequestAsync(session, PracticePromptBuilder.NextQuestion(session, [.. asked.Select(a => a.Question)], PracticeProvider), cancellationToken);
        return StreamQuestionAsync(sessionId, request, cancellationToken);
    }

    /// <summary>
    /// Waits for the last words, takes the answer and checks it, then streams the score as the model writes it
    /// (#78): the score first, then the feedback, then the saved attempt. An unreadable reply is retried once, with
    /// a restart part first; if scoring fails or the caller stops reading, the answer is kept for another try.
    /// </summary>
    public async Task<IAsyncEnumerable<PracticeScorePart>> FinishAsync(Guid ownerId, Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await LoadLiveMockAsync(ownerId, sessionId, cancellationToken);
        await WaitForLastWordsAsync(sessionId, cancellationToken);
        var collected = collector.Take(sessionId);
        if (collected is null || string.IsNullOrWhiteSpace(collected.Answer))
        {
            if (collected is not null)
            {
                collector.Restore(sessionId, collected); // keep the question open so the candidate can still answer
            }

            throw new DomainException(NoAnswerMessage);
        }

        var request = await RequestAsync(session, PracticePromptBuilder.ScoreAnswer(session, collected.Question, collected.Answer), cancellationToken);
        return StreamScoreAsync(sessionId, collected, request, cancellationToken);
    }

    public async Task<IReadOnlyList<PracticeAttemptDto>> ListAsync(Guid ownerId, Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await sessions.LoadOwnedAsync(ownerId, sessionId, cancellationToken);
        RequireMock(session);
        var saved = await attempts.ListAsync(sessionId, cancellationToken);
        return [.. saved.OrderBy(a => a.CreatedAt).Select(PracticeAttemptDto.From)];
    }

    /// <summary>
    /// "Done answering" often comes just as the candidate stops talking, while their last sentence is still being
    /// transcribed. Gives it up to <see cref="LastWordsWaitMs"/>, keeping the question open so the mic still listens.
    /// </summary>
    private async Task WaitForLastWordsAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (!collector.IsOpen(sessionId))
        {
            return;
        }

        var spokenUpToMs = audioClock.PositionMs(sessionId, Speaker.Candidate);
        for (var waitedMs = 0;
             waitedMs < LastWordsWaitMs && audioClock.TranscribedThroughMs(sessionId, Speaker.Candidate) < spokenUpToMs;
             waitedMs += LastWordsPollMs)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(LastWordsPollMs), clock, cancellationToken);
        }
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

    private async IAsyncEnumerable<PracticeScorePart> StreamScoreAsync(
        Guid sessionId,
        CollectedAnswer collected,
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var saved = false;
        try
        {
            for (var attempt = 0; attempt <= ScoreRetries; attempt++)
            {
                if (attempt > 0)
                {
                    yield return new PracticeScorePart(string.Empty, Restart: true);
                }

                var reader = new ScoreReplyReader();
                await foreach (var token in llmProviders.Resolve(PracticeProvider).StreamAsync(request, cancellationToken))
                {
                    var read = reader.Add(token);
                    if (read.Score is not null || read.Feedback.Length > 0)
                    {
                        yield return new PracticeScorePart(read.Feedback, read.Score);
                    }
                }

                if (PracticePromptBuilder.ParseFeedback(reader.Reply) is { } feedback)
                {
                    var created = PracticeAttempt.Create(
                        sessionId, collected.Question, collected.Answer, feedback.Score, feedback.Feedback, clock.GetUtcNow());
                    attempts.Add(created);
                    await unitOfWork.SaveChangesAsync(cancellationToken);
                    saved = true;
                    yield return new PracticeScorePart(string.Empty, Attempt: PracticeAttemptDto.From(created));
                    yield break;
                }
            }

            throw new LlmUnavailableException(UnreadableFeedbackMessage);
        }
        finally
        {
            if (!saved)
            {
                collector.Restore(sessionId, collected); // a failed or abandoned score can be tried again
            }
        }
    }

    private async IAsyncEnumerable<PracticeQuestionPart> StreamQuestionAsync(
        Guid sessionId,
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var reply = new StringBuilder();
        await foreach (var token in llmProviders.Resolve(PracticeProvider).StreamAsync(request, cancellationToken))
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
