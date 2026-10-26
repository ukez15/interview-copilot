using InterviewCopilot.Domain.Exceptions;

namespace InterviewCopilot.Domain.Entities;

/// <summary>
/// A browser extension paired with this account through a one-time code (#33, FR-18). Only a hash of its
/// token is stored; the token itself is shown to the extension once.
/// </summary>
public sealed class PairedBrowser
{
    public const int MaxNameLength = 100;

    private PairedBrowser()
    {
        Name = string.Empty;
        TokenHash = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public string Name { get; private set; }

    /// <summary>SHA-256 of the pairing token, hex encoded.</summary>
    public string TokenHash { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? LastUsedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public bool IsRevoked => RevokedAt is not null;

    public static PairedBrowser Create(Guid userId, string name, string tokenHash, DateTimeOffset now)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > MaxNameLength)
        {
            throw new DomainException($"Browser names must be 1 to {MaxNameLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(tokenHash))
        {
            throw new DomainException("A paired browser needs a token hash.");
        }

        return new PairedBrowser
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = trimmed,
            TokenHash = tokenHash,
            CreatedAt = now,
        };
    }

    public void MarkUsed(DateTimeOffset now) => LastUsedAt = now;

    /// <summary>Revoking twice keeps the first time.</summary>
    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;
}
