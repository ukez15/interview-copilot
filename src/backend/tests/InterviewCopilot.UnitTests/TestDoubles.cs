using System.Runtime.CompilerServices;
using InterviewCopilot.Application.Auth;
using InterviewCopilot.Application.Common.Interfaces;
using InterviewCopilot.Application.Providers;
using InterviewCopilot.Domain.Entities;

namespace InterviewCopilot.UnitTests;

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public static readonly DateTimeOffset Default = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    public FixedClock()
        : this(Default)
    {
    }

    public override DateTimeOffset GetUtcNow() => now;
}

internal sealed class InMemoryStore : ISessionRepository, IUserAccountRepository, ITranscriptRepository, IPracticeAttemptRepository, IUnitOfWork
{
    public List<Session> Sessions { get; } = [];

    public List<UserAccount> Accounts { get; } = [];

    public List<TranscriptSegment> Segments { get; } = [];

    public List<SuggestedAnswer> Answers { get; } = [];

    public List<PracticeAttempt> Attempts { get; } = [];

    public int SaveCount { get; private set; }

    public Task<Session?> GetAsync(Guid sessionId, CancellationToken cancellationToken) =>
        Task.FromResult(Sessions.Find(s => s.Id == sessionId));

    public Task<IReadOnlyList<Session>> ListByOwnerAsync(Guid ownerId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Session>>([.. Sessions.Where(s => s.OwnerId == ownerId)]);

    public void Add(Session session) => Sessions.Add(session);

    public Task<UserAccount?> FindByUsernameAsync(string username, CancellationToken cancellationToken) =>
        Task.FromResult(Accounts.Find(a => string.Equals(a.Username, username, StringComparison.OrdinalIgnoreCase)));

    public void Add(UserAccount account) => Accounts.Add(account);

    public Task<IReadOnlyList<TranscriptSegment>> GetSegmentsAsync(Guid sessionId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TranscriptSegment>>([.. Segments.Where(s => s.SessionId == sessionId)]);

    public Task<IReadOnlyList<SuggestedAnswer>> GetAnswersAsync(Guid sessionId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SuggestedAnswer>>([.. Answers.Where(a => a.SessionId == sessionId)]);

    public void AddSegment(TranscriptSegment segment) => Segments.Add(segment);

    public void AddAnswer(SuggestedAnswer answer) => Answers.Add(answer);

    public Task<IReadOnlyList<PracticeAttempt>> ListAsync(Guid sessionId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PracticeAttempt>>([.. Attempts.Where(a => a.SessionId == sessionId).OrderBy(a => a.CreatedAt)]);

    public void Add(PracticeAttempt attempt) => Attempts.Add(attempt);

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        SaveCount++;
        return Task.CompletedTask;
    }
}

internal sealed class FakeLlm(params string[] tokens) : ILlmProvider
{
    public LlmRequest? LastRequest { get; private set; }

    public Exception? ThrowOnStream { get; init; }

    public string Name => ProviderNames.Ollama; // stands in for the configured default provider

    public async IAsyncEnumerable<string> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        LastRequest = request;
        if (ThrowOnStream is not null)
        {
            throw ThrowOnStream;
        }

        foreach (var token in tokens)
        {
            await Task.Yield();
            yield return token;
        }
    }
}

/// <summary>Reversible fake: "hash:" + password.</summary>
internal sealed class FakePasswordHasher : IPasswordHasher
{
    public string Hash(string password) => "hash:" + password;

    public bool Verify(string passwordHash, string password) => passwordHash == "hash:" + password;
}
