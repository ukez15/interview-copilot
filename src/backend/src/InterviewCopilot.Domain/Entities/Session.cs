using InterviewCopilot.Domain.Enums;
using InterviewCopilot.Domain.Exceptions;

namespace InterviewCopilot.Domain.Entities;

/// <summary>One conversation the copilot assists with, plus its answer settings (FR-01..FR-06).</summary>
public sealed class Session
{
    private readonly List<SessionDocument> _documents = [];

    private Session()
    {
        Title = string.Empty;
        SessionPrompt = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid OwnerId { get; private set; }

    public string Title { get; private set; }

    public SessionMode Mode { get; private set; }

    public AnswerLength AnswerLength { get; private set; }

    public AnswerFramework Framework { get; private set; }

    /// <summary>Type pinned before the session; auto-detection may override it per question.</summary>
    public QuestionType? PinnedQuestionType { get; private set; }

    /// <summary>Who the user is talking to and how to answer (FR-06).</summary>
    public string SessionPrompt { get; private set; }

    public SessionStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    /// <summary>Answer detected interviewer questions automatically (#22). On by default; the user can pause it.</summary>
    public bool AutoAnswer { get; private set; }

    public IReadOnlyList<SessionDocument> Documents => _documents;

    public static Session Create(Guid ownerId, string title, SessionMode mode, DateTimeOffset now)
    {
        var session = new Session
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            Mode = mode,
            Status = SessionStatus.Draft,
            CreatedAt = now,
            AnswerLength = AnswerLength.Balanced,
            Framework = AnswerFramework.None,
            AutoAnswer = true,
        };
        session.Rename(title);
        return session;
    }

    public void Rename(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new DomainException("A session needs a title.");
        }

        if (title.Length > SessionLimits.MaxTitleLength)
        {
            throw new DomainException($"Session titles can be at most {SessionLimits.MaxTitleLength} characters.");
        }

        Title = title.Trim();
    }

    public void ConfigureAnswers(
        AnswerLength length,
        AnswerFramework framework,
        QuestionType? pinnedQuestionType,
        string? sessionPrompt)
    {
        var prompt = sessionPrompt?.Trim() ?? string.Empty;
        if (prompt.Length > SessionLimits.MaxSessionPromptLength)
        {
            throw new DomainException(
                $"The session prompt can be at most {SessionLimits.MaxSessionPromptLength} characters.");
        }

        AnswerLength = length;
        Framework = framework;
        PinnedQuestionType = pinnedQuestionType;
        SessionPrompt = prompt;
    }

    /// <summary>Attaches a document, enforcing the 10-document limit (FR-02).</summary>
    public SessionDocument AttachDocument(string fileName, string content, DateTimeOffset now)
    {
        if (_documents.Count >= SessionLimits.MaxDocumentsPerSession)
        {
            throw new DomainException(
                $"A session can have at most {SessionLimits.MaxDocumentsPerSession} documents. Remove one first.");
        }

        var document = SessionDocument.Create(Id, fileName, content, now);
        _documents.Add(document);
        return document;
    }

    public void RemoveDocument(Guid documentId)
    {
        var removed = _documents.RemoveAll(d => d.Id == documentId);
        if (removed == 0)
        {
            throw new DomainException("That document isn't attached to this session.");
        }
    }

    /// <summary>Pauses or resumes automatic answers. Allowed in any state; it only matters while Live.</summary>
    public void SetAutoAnswer(bool enabled) => AutoAnswer = enabled;

    public void Start(DateTimeOffset now)
    {
        if (Status != SessionStatus.Draft)
        {
            throw new DomainException("Only a session that hasn't started yet can be started.");
        }

        Status = SessionStatus.Live;
        StartedAt = now;
    }

    public void End(DateTimeOffset now)
    {
        if (Status != SessionStatus.Live)
        {
            throw new DomainException("Only a live session can be ended.");
        }

        Status = SessionStatus.Ended;
        EndedAt = now;
    }
}
