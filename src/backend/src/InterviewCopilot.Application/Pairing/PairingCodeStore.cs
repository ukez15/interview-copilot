using System.Security.Cryptography;

namespace InterviewCopilot.Application.Pairing;

/// <summary>A freshly issued pairing code and when it stops working.</summary>
public sealed record PairingCodeDto(string Code, DateTimeOffset ExpiresAt);

/// <summary>
/// Pairing codes live in memory only (#33): single use, one active code per user, valid for
/// <see cref="Lifetime"/>. Registered as a singleton.
/// </summary>
public sealed class PairingCodeStore(TimeProvider clock)
{
    public const int CodeLength = 6;

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, (Guid UserId, DateTimeOffset ExpiresAt)> _codes = new(StringComparer.Ordinal);

    /// <summary>Issues a new code for the user, replacing any code they still had.</summary>
    public PairingCodeDto Issue(Guid userId)
    {
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            Purge(now, userId);
            string code;
            do
            {
                code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            }
            while (_codes.ContainsKey(code));

            var expiresAt = now + Lifetime;
            _codes[code] = (userId, expiresAt);
            return new PairingCodeDto(code, expiresAt);
        }
    }

    /// <summary>Returns the owner of a valid code and uses it up; null when unknown, expired or already used.</summary>
    public Guid? TryConsume(string code)
    {
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            if (!_codes.Remove(code, out var entry))
            {
                return null;
            }

            return entry.ExpiresAt > now ? entry.UserId : null;
        }
    }

    private void Purge(DateTimeOffset now, Guid userId)
    {
        foreach (var (code, entry) in _codes.ToList())
        {
            if (entry.UserId == userId || entry.ExpiresAt <= now)
            {
                _codes.Remove(code);
            }
        }
    }
}
