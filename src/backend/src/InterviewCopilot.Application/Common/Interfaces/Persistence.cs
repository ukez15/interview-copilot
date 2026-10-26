using InterviewCopilot.Domain.Entities;

namespace InterviewCopilot.Application.Common.Interfaces;

public interface ISessionRepository
{
    Task<Session?> GetAsync(Guid sessionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Session>> ListByOwnerAsync(Guid ownerId, CancellationToken cancellationToken);

    void Add(Session session);
}

public interface IUserAccountRepository
{
    Task<UserAccount?> FindByUsernameAsync(string username, CancellationToken cancellationToken);

    void Add(UserAccount account);
}

public interface ITranscriptRepository
{
    Task<IReadOnlyList<TranscriptSegment>> GetSegmentsAsync(Guid sessionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SuggestedAnswer>> GetAnswersAsync(Guid sessionId, CancellationToken cancellationToken);

    void AddSegment(TranscriptSegment segment);

    void AddAnswer(SuggestedAnswer answer);
}

public interface IUserPreferencesRepository
{
    Task<UserPreferences?> GetAsync(Guid userId, CancellationToken cancellationToken);

    void Add(UserPreferences preferences);
}

/// <summary>Browser extensions paired with an account (#33).</summary>
public interface IPairedBrowserRepository
{
    Task<PairedBrowser?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<PairedBrowser?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken);

    Task<IReadOnlyList<PairedBrowser>> ListAsync(Guid userId, CancellationToken cancellationToken);

    void Add(PairedBrowser browser);
}

/// <summary>Scored answers from mock interview mode (#26).</summary>
public interface IPracticeAttemptRepository
{
    /// <summary>Returns the session's attempts, oldest first.</summary>
    Task<IReadOnlyList<PracticeAttempt>> ListAsync(Guid sessionId, CancellationToken cancellationToken);

    void Add(PracticeAttempt attempt);
}

public interface IUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
