using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using InterviewCopilot.Application.Common.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace InterviewCopilot.Application.Audio;

/// <summary>Picks, once per audio stream, the local speech model a session uses: its owner's choice (#85).</summary>
public interface ISpeechModelSelector
{
    /// <summary>The owner's chosen model file, or null for the configured default. Fixed until <see cref="Forget"/>.</summary>
    /// <param name="sessionId">The session whose audio is being transcribed.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The model file name, or null.</returns>
    ValueTask<string?> ForSessionAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>Drops the cached choice, so the session's next stream picks up a changed preference.</summary>
    /// <param name="sessionId">The session whose audio stream ended.</param>
    void Forget(Guid sessionId);
}

/// <summary>Default <see cref="ISpeechModelSelector"/>: reads the owner's preferences through a scope, like the speech-to-text router.</summary>
public sealed class SpeechModelSelector(IServiceScopeFactory scopes) : ISpeechModelSelector
{
    private readonly ConcurrentDictionary<Guid, Lazy<Task<Choice?>>> _choices = new();

    /// <inheritdoc />
    public async ValueTask<string?> ForSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var lookup = _choices.GetOrAdd(sessionId, id => new Lazy<Task<Choice?>>(() => LookUpAsync(id)));
        var choice = await lookup.Value.WaitAsync(cancellationToken);
        if (choice is null)
        {
            // The session or its preferences couldn't be read: use the default now and look again next time.
            _choices.TryRemove(new KeyValuePair<Guid, Lazy<Task<Choice?>>>(sessionId, lookup));
            return null;
        }

        return choice.Model;
    }

    /// <inheritdoc />
    public void Forget(Guid sessionId) => _choices.TryRemove(sessionId, out _);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A failed lookup must never break transcription; the default model is used and the lookup retried.")]
    private async Task<Choice?> LookUpAsync(Guid sessionId)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var session = await scope.ServiceProvider.GetRequiredService<ISessionRepository>()
                .GetAsync(sessionId, CancellationToken.None);
            if (session is null)
            {
                return null;
            }

            var preferences = await scope.ServiceProvider.GetRequiredService<IUserPreferencesRepository>()
                .GetAsync(session.OwnerId, CancellationToken.None);
            return new Choice(preferences?.SpeechModel);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>A looked-up choice; its model is null when the owner never chose.</summary>
    private sealed record Choice(string? Model);
}
