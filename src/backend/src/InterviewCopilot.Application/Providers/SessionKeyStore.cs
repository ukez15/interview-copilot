using System.Collections.Concurrent;

namespace InterviewCopilot.Application.Providers;

/// <summary>
/// BYOK keys handed over for one live session (owner decision, wave 3). Held in memory only: never
/// persisted or logged, cleared when the session ends, and gone when the backend restarts.
/// </summary>
public interface ISessionKeyStore
{
    void Store(Guid sessionId, string provider, string key);

    string? TryGet(Guid sessionId, string provider);

    void Clear(Guid sessionId);
}

public sealed class InMemorySessionKeyStore : ISessionKeyStore
{
    private readonly ConcurrentDictionary<(Guid SessionId, string Provider), string> _keys = new();

    public void Store(Guid sessionId, string provider, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _keys[(sessionId, Normalize(provider))] = key;
    }

    public string? TryGet(Guid sessionId, string provider) =>
        _keys.TryGetValue((sessionId, Normalize(provider)), out var key) ? key : null;

    public void Clear(Guid sessionId)
    {
        foreach (var entry in _keys.Keys.Where(k => k.SessionId == sessionId))
        {
            _keys.TryRemove(entry, out _);
        }
    }

    private static string Normalize(string provider) => provider.Trim().ToLowerInvariant();
}
