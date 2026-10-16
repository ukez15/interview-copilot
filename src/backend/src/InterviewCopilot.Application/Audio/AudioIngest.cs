using System.Collections.Concurrent;
using InterviewCopilot.Application.Common.Exceptions;
using InterviewCopilot.Application.Sessions;
using InterviewCopilot.Domain.Entities;
using InterviewCopilot.Domain.Enums;

namespace InterviewCopilot.Application.Audio;

/// <summary>One 100 ms chunk of one speaker's audio: 16 kHz mono PCM16 LE (docs/atlas-gotcha/audio-capture-windows.md).</summary>
public readonly record struct AudioFrame(Speaker Speaker, ReadOnlyMemory<byte> Pcm16);

/// <summary>Wire format: [speaker byte][PCM16 LE...]. Speaker 0 = Interviewer, 1 = Candidate.</summary>
public static class AudioFrameParser
{
    public const int SampleRate = 16_000;
    public const int FrameSamples = 1_600;

    /// <summary>Largest accepted message: one speaker byte plus 100 ms of PCM16.</summary>
    public const int MaxMessageBytes = 1 + (FrameSamples * 2);

    public static bool TryParse(ReadOnlyMemory<byte> message, out AudioFrame frame)
    {
        frame = default;
        var span = message.Span;
        if (span.Length < 3 || span.Length > MaxMessageBytes || (span.Length - 1) % 2 != 0)
        {
            return false;
        }

        var speaker = span[0] switch
        {
            0 => Speaker.Interviewer,
            1 => Speaker.Candidate,
            _ => (Speaker?)null,
        };
        if (speaker is null)
        {
            return false;
        }

        frame = new AudioFrame(speaker.Value, message[1..]);
        return true;
    }
}

/// <summary>Consumes incoming audio. Transcription plugs in here as another sink.</summary>
public interface IAudioFrameSink
{
    ValueTask OnFrameAsync(Guid sessionId, AudioFrame frame, CancellationToken cancellationToken);

    /// <summary>The session's audio socket closed (normally or not). Default: nothing to do (#43 implements flushing).</summary>
    /// <param name="sessionId">The session whose stream ended.</param>
    /// <param name="cancellationToken">Cancels the cleanup.</param>
    /// <returns>A task that completes when the sink has handled the end of the stream.</returns>
    ValueTask OnStreamEndedAsync(Guid sessionId, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

/// <summary>Notified after each transcript segment is saved and published (#22 auto-answer plugs in here).</summary>
public interface ITranscriptSegmentListener
{
    /// <summary>Handles a new segment. Exceptions are caught and logged by the worker.</summary>
    /// <param name="segment">The saved segment.</param>
    /// <param name="cancellationToken">Stops the work when the host shuts down.</param>
    /// <returns>A task that completes when the listener is done with the segment.</returns>
    ValueTask OnSegmentAsync(TranscriptSegment segment, CancellationToken cancellationToken);
}

public sealed record AudioStatsDto(long InterviewerBytes, long CandidateBytes, long Frames, DateTimeOffset? LastFrameAt);

/// <summary>Counts what arrived per session so the UI can show that both tracks are flowing.</summary>
public sealed class AudioStatsSink(TimeProvider clock) : IAudioFrameSink
{
    private readonly ConcurrentDictionary<Guid, Counters> _sessions = new();

    public ValueTask OnFrameAsync(Guid sessionId, AudioFrame frame, CancellationToken cancellationToken)
    {
        var counters = _sessions.GetOrAdd(sessionId, _ => new Counters());
        counters.Add(frame.Speaker, frame.Pcm16.Length, clock.GetUtcNow());
        return ValueTask.CompletedTask;
    }

    public AudioStatsDto Get(Guid sessionId) =>
        _sessions.TryGetValue(sessionId, out var counters) ? counters.Snapshot() : new AudioStatsDto(0, 0, 0, null);

    private sealed class Counters
    {
        private readonly Lock _lock = new();
        private long _interviewer;
        private long _candidate;
        private long _frames;
        private DateTimeOffset? _last;

        public void Add(Speaker speaker, int bytes, DateTimeOffset at)
        {
            lock (_lock)
            {
                if (speaker == Speaker.Interviewer)
                {
                    _interviewer += bytes;
                }
                else
                {
                    _candidate += bytes;
                }

                _frames++;
                _last = at;
            }
        }

        public AudioStatsDto Snapshot()
        {
            lock (_lock)
            {
                return new AudioStatsDto(_interviewer, _candidate, _frames, _last);
            }
        }
    }
}

/// <summary>Checks who may stream audio and fans frames out to every sink.</summary>
public sealed class AudioIngestService(SessionService sessions, IEnumerable<IAudioFrameSink> sinks)
{
    /// <summary>Throws NotFound (not the caller's) or Conflict (session not live).</summary>
    public async Task AuthorizeAsync(Guid ownerId, Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await sessions.GetAsync(ownerId, sessionId, cancellationToken);
        if (session.Status != SessionStatus.Live)
        {
            throw new ConflictException("Audio can only be streamed to a live session. Start the session first.");
        }
    }

    public async ValueTask IngestAsync(Guid sessionId, AudioFrame frame, CancellationToken cancellationToken)
    {
        foreach (var sink in sinks)
        {
            await sink.OnFrameAsync(sessionId, frame, cancellationToken);
        }
    }

    /// <summary>Tells every sink the session's audio socket closed, for any reason.</summary>
    /// <param name="sessionId">The session whose stream ended.</param>
    /// <param name="cancellationToken">Cancels the cleanup.</param>
    /// <returns>A task that completes when every sink has handled the end.</returns>
    public async ValueTask EndStreamAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        foreach (var sink in sinks)
        {
            await sink.OnStreamEndedAsync(sessionId, cancellationToken);
        }
    }
}
