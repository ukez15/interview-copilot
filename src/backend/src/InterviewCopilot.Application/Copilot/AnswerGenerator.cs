using System.Runtime.CompilerServices;
using System.Text;
using InterviewCopilot.Application.Common.Exceptions;
using InterviewCopilot.Application.Common.Interfaces;
using InterviewCopilot.Application.Events;
using InterviewCopilot.Application.Providers;
using InterviewCopilot.Domain.Entities;
using InterviewCopilot.Domain.Enums;
using Microsoft.Extensions.Options;

namespace InterviewCopilot.Application.Copilot;

public sealed record AnswerRequest(
    Guid SessionId,
    string Question,
    AnswerModifier Modifier = AnswerModifier.None,
    string? PreviousAnswer = null,
    int PreviousVersion = 0,
    string? ApiKey = null);

public sealed record AnswerHeader(QuestionType QuestionType, int Version);

/// <summary>Detect type → select context → build prompt → stream from the model → persist (FR-08..FR-10).</summary>
public sealed class AnswerGenerator(
    ISessionRepository sessions,
    ITranscriptRepository transcripts,
    IUnitOfWork unitOfWork,
    ILlmProviderResolver llmProviders,
    ISessionKeyStore sessionKeys,
    IOptions<LlmOptions> llmOptions,
    TimeProvider clock,
    ISessionEventPublisher events,
    IUserPreferencesRepository preferences)
{
    public const int RecentSegmentCount = 12;

    public async Task<(AnswerHeader Header, IAsyncEnumerable<string> Tokens)> GenerateAsync(
        AnswerRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var session = await sessions.GetAsync(request.SessionId, cancellationToken)
            ?? throw new NotFoundException("Session not found.");

        var questionType = QuestionTypeResolver.Resolve(
            session.PinnedQuestionType,
            QuestionClassifier.Classify(request.Question));

        var segments = await transcripts.GetSegmentsAsync(session.Id, cancellationToken);
        var input = new AnswerPromptInput(
            session,
            request.Question,
            questionType,
            DocumentContextSelector.Select(session.Documents, request.Question),
            [.. segments.TakeLast(RecentSegmentCount)],
            request.Modifier,
            request.PreviousAnswer);

        var options = llmOptions.Value;
        var owner = await preferences.GetAsync(session.OwnerId, cancellationToken);
        var choice = ChatModelChoice.For(owner, options);
        var llm = llmProviders.Resolve(choice.Provider);

        // A key handed to the live session wins; a per-request key still works for manual answers (#30).
        var apiKey = sessionKeys.TryGet(session.Id, choice.Provider) ?? request.ApiKey;
        var llmRequest = new LlmRequest(choice.Model, AnswerPromptBuilder.Build(input), options.Temperature, apiKey);
        var version = request.PreviousVersion + 1;

        return (new AnswerHeader(questionType, version),
            StreamAndPersistAsync(llm, session.Id, request, questionType, version, llmRequest, cancellationToken));
    }

    private async IAsyncEnumerable<string> StreamAndPersistAsync(
        ILlmProvider llm,
        Guid sessionId,
        AnswerRequest request,
        QuestionType questionType,
        int version,
        LlmRequest llmRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var answer = new StringBuilder();
        await foreach (var token in llm.StreamAsync(llmRequest, cancellationToken))
        {
            answer.Append(token);
            events.Publish(sessionId, SessionEventTypes.AnswerDelta, new { token, version, questionType });
            yield return token;
        }

        var suggested = SuggestedAnswer.Create(
            sessionId,
            request.Question,
            questionType,
            answer.ToString(),
            version,
            request.Modifier,
            clock.GetUtcNow());
        transcripts.AddAnswer(suggested);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        events.Publish(sessionId, SessionEventTypes.AnswerDone, new { version, questionType, answerId = suggested.Id });
    }
}
