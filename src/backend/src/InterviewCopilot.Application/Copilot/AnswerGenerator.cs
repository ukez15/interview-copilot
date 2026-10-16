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
    ILlmProvider llm,
    IOptions<LlmOptions> llmOptions,
    TimeProvider clock,
    ISessionEventPublisher events)
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
        var llmRequest = new LlmRequest(options.Model, AnswerPromptBuilder.Build(input), options.Temperature, request.ApiKey);
        var version = request.PreviousVersion + 1;

        return (new AnswerHeader(questionType, version),
            StreamAndPersistAsync(session.Id, request, questionType, version, llmRequest, cancellationToken));
    }

    private async IAsyncEnumerable<string> StreamAndPersistAsync(
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
