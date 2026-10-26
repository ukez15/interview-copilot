using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using InterviewCopilot.Application.Common.Exceptions;
using InterviewCopilot.Application.Common.Interfaces;
using InterviewCopilot.Domain.Entities;
using InterviewCopilot.Domain.Exceptions;

namespace InterviewCopilot.Application.Pairing;

public sealed record PairedBrowserDto(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

/// <summary>Who a valid pairing token belongs to.</summary>
public sealed record PairedIdentity(Guid UserId, Guid BrowserId);

/// <summary>Pairs browser extensions with the signed-in account (#33, FR-18).</summary>
public sealed class PairingService(
    IPairedBrowserRepository browsers,
    IUnitOfWork unitOfWork,
    PairingCodeStore codes,
    TimeProvider clock)
{
    public const string InvalidCodeMessage = "That pairing code is invalid or expired.";

    /// <summary>LastUsedAt is refreshed at most this often per browser.</summary>
    public static readonly TimeSpan LastUsedInterval = TimeSpan.FromMinutes(5);

    private const int TokenBytes = 32;

    /// <summary>SHA-256 of the token as lower-case hex. This is all that is stored.</summary>
    public static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public PairingCodeDto IssueCode(Guid userId) => codes.Issue(userId);

    /// <summary>Uses up a code and returns the new token. The token is not recoverable afterwards.</summary>
    public async Task<string> ClaimAsync(string code, string name, CancellationToken cancellationToken)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > PairedBrowser.MaxNameLength)
        {
            throw new DomainException($"Browser names must be 1 to {PairedBrowser.MaxNameLength} characters.");
        }

        var userId = IsWellFormed(code) ? codes.TryConsume(code) : null;
        if (userId is null)
        {
            throw new DomainException(InvalidCodeMessage);
        }

        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));
        browsers.Add(PairedBrowser.Create(userId.Value, trimmed, HashToken(token), clock.GetUtcNow()));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return token;
    }

    public async Task<IReadOnlyList<PairedBrowserDto>> ListAsync(Guid userId, CancellationToken cancellationToken)
    {
        var all = await browsers.ListAsync(userId, cancellationToken);
        return [.. all.Where(b => !b.IsRevoked).Select(b => new PairedBrowserDto(b.Id, b.Name, b.CreatedAt, b.LastUsedAt))];
    }

    public async Task RevokeAsync(Guid userId, Guid browserId, CancellationToken cancellationToken)
    {
        var browser = await browsers.GetAsync(browserId, cancellationToken);
        if (browser is null || browser.UserId != userId)
        {
            throw new NotFoundException("Paired browser not found.");
        }

        browser.Revoke(clock.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Resolves a pairing token; null when unknown or revoked. Refreshes LastUsedAt at most every 5 minutes.</summary>
    public async Task<PairedIdentity?> AuthenticateAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var browser = await browsers.FindByTokenHashAsync(HashToken(token), cancellationToken);
        if (browser is null || browser.IsRevoked)
        {
            return null;
        }

        var now = clock.GetUtcNow();
        if (browser.LastUsedAt is not { } last || now - last >= LastUsedInterval)
        {
            browser.MarkUsed(now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return new PairedIdentity(browser.UserId, browser.Id);
    }

    private static bool IsWellFormed(string? code) =>
        code is { Length: PairingCodeStore.CodeLength } && code.All(char.IsAsciiDigit);
}
