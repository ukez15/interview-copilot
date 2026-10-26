using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using InterviewCopilot.Application.Common.Interfaces;
using InterviewCopilot.Application.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace InterviewCopilot.Application.Audio;

/// <summary>Decides, once per live session, whether its audio goes to Deepgram streaming or to the local Whisper worker.</summary>
public interface ISpeechToTextRouter
{
    /// <summary>
    /// True when the session's speech-to-text provider (the owner's choice, else the configured default) is
    /// Deepgram and Deepgram is enabled in configuration. The answer is fixed until <see cref="Forget"/>.
    /// </summary>
    /// <param name="sessionId">The session whose audio is arriving.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>True when the session uses Deepgram.</returns>
    ValueTask<bool> UsesDeepgramAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>Drops the cached decision, so a later stream for the session decides again.</summary>
    /// <param name="sessionId">The session whose audio stream ended.</param>
    void Forget(Guid sessionId);
}

/// <summary>Default <see cref="ISpeechToTextRouter"/>: reads the owner's preferences through a scope on the first frame.</summary>
public sealed class SpeechToTextRouter : ISpeechToTextRouter
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptions<TranscriptionOptions> _options;
    private readonly ConcurrentDictionary<Guid, Lazy<Task<bool?>>> _decisions = new();

    /// <summary>Creates the router.</summary>
    /// <param name="scopes">Creates a scope for the scoped repositories.</param>
    /// <param name="options">Transcription configuration (default provider and whether Deepgram is enabled).</param>
    public SpeechToTextRouter(IServiceScopeFactory scopes, IOptions<TranscriptionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(options);
        _scopes = scopes;
        _options = options;
    }

    /// <inheritdoc />
    public async ValueTask<bool> UsesDeepgramAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        // Deepgram off in configuration is the zero-cost default: no lookup, nothing cached.
        if (!_options.Value.Providers.Deepgram.Enabled)
        {
            return false;
        }

        var decision = _decisions.GetOrAdd(sessionId, id => new Lazy<Task<bool?>>(() => DecideAsync(id)));
        var result = await decision.Value.WaitAsync(cancellationToken);
        if (result is null)
        {
            // The session or its preferences couldn't be read: use Whisper now, but decide again on the next frame.
            _decisions.TryRemove(new KeyValuePair<Guid, Lazy<Task<bool?>>>(sessionId, decision));
            return false;
        }

        return result.Value;
    }

    /// <inheritdoc />
    public void Forget(Guid sessionId) => _decisions.TryRemove(sessionId, out _);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A failed lookup must never break audio ingest; the frame is treated as Whisper and the decision is retried.")]
    private async Task<bool?> DecideAsync(Guid sessionId)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var session = await scope.ServiceProvider.GetRequiredService<ISessionRepository>()
                .GetAsync(sessionId, CancellationToken.None);
            if (session is null)
            {
                return null;
            }

            var preferences = await scope.ServiceProvider.GetRequiredService<IUserPreferencesRepository>()
                .GetAsync(session.OwnerId, CancellationToken.None);
            var provider = preferences?.SpeechToTextProvider ?? _options.Value.Provider;
            return string.Equals(provider?.Trim(), ProviderNames.Deepgram, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
