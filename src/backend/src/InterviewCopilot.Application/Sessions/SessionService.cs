using InterviewCopilot.Application.Common.Exceptions;
using InterviewCopilot.Application.Common.Interfaces;
using InterviewCopilot.Application.Events;
using InterviewCopilot.Domain.Entities;
using InterviewCopilot.Domain.Enums;

namespace InterviewCopilot.Application.Sessions;

public sealed record CreateSessionCommand(
    Guid OwnerId,
    string Title,
    SessionMode Mode,
    AnswerLength AnswerLength,
    AnswerFramework Framework,
    QuestionType? PinnedQuestionType,
    string? SessionPrompt);

public sealed record UpdateSessionSettingsCommand(
    AnswerLength AnswerLength,
    AnswerFramework Framework,
    QuestionType? PinnedQuestionType,
    string? SessionPrompt);

public sealed record DocumentSummaryDto(Guid Id, string FileName, int CharacterCount, DateTimeOffset CreatedAt);

public sealed record SessionDto(
    Guid Id,
    string Title,
    SessionMode Mode,
    SessionStatus Status,
    AnswerLength AnswerLength,
    AnswerFramework Framework,
    QuestionType? PinnedQuestionType,
    string SessionPrompt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    IReadOnlyList<DocumentSummaryDto> Documents,
    bool AutoAnswer)
{
    public static SessionDto From(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new SessionDto(
            session.Id,
            session.Title,
            session.Mode,
            session.Status,
            session.AnswerLength,
            session.Framework,
            session.PinnedQuestionType,
            session.SessionPrompt,
            session.CreatedAt,
            session.StartedAt,
            session.EndedAt,
            [.. session.Documents.Select(d => new DocumentSummaryDto(d.Id, d.FileName, d.Content.Length, d.CreatedAt))],
            session.AutoAnswer);
    }
}

/// <summary>Session setup and lifecycle (FR-01..FR-06).</summary>
public sealed class SessionService(
    ISessionRepository sessions,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ISessionEventPublisher? events = null)
{
    public async Task<SessionDto> CreateAsync(CreateSessionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var session = Session.Create(command.OwnerId, command.Title, command.Mode, clock.GetUtcNow());
        session.ConfigureAnswers(command.AnswerLength, command.Framework, command.PinnedQuestionType, command.SessionPrompt);
        sessions.Add(session);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return SessionDto.From(session);
    }

    public async Task<SessionDto> GetAsync(Guid ownerId, Guid sessionId, CancellationToken cancellationToken) =>
        SessionDto.From(await LoadOwnedAsync(ownerId, sessionId, cancellationToken));

    public async Task<IReadOnlyList<SessionDto>> ListAsync(Guid ownerId, CancellationToken cancellationToken)
    {
        var owned = await sessions.ListByOwnerAsync(ownerId, cancellationToken);
        return [.. owned.Select(SessionDto.From)];
    }

    public async Task<SessionDto> UpdateSettingsAsync(
        Guid ownerId,
        Guid sessionId,
        UpdateSessionSettingsCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var session = await LoadOwnedAsync(ownerId, sessionId, cancellationToken);
        session.ConfigureAnswers(command.AnswerLength, command.Framework, command.PinnedQuestionType, command.SessionPrompt);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return SessionDto.From(session);
    }

    public async Task<SessionDto> StartAsync(Guid ownerId, Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await LoadOwnedAsync(ownerId, sessionId, cancellationToken);
        session.Start(clock.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return SessionDto.From(session);
    }

    public async Task<SessionDto> EndAsync(Guid ownerId, Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await LoadOwnedAsync(ownerId, sessionId, cancellationToken);
        session.End(clock.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return SessionDto.From(session);
    }

    /// <summary>Pauses or resumes automatic answers and tells everyone watching the session (#22).</summary>
    public async Task<SessionDto> SetAutoAnswerAsync(Guid ownerId, Guid sessionId, bool enabled, CancellationToken cancellationToken)
    {
        var session = await LoadOwnedAsync(ownerId, sessionId, cancellationToken);
        session.SetAutoAnswer(enabled);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        events?.Publish(session.Id, SessionEventTypes.AutoAnswerState, new { enabled });
        return SessionDto.From(session);
    }

    internal async Task<Session> LoadOwnedAsync(Guid ownerId, Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await sessions.GetAsync(sessionId, cancellationToken);
        if (session is null || session.OwnerId != ownerId)
        {
            throw new NotFoundException("Session not found.");
        }

        return session;
    }
}
