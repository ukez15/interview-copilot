using System.Collections.Concurrent;
using System.Threading.Channels;
using InterviewCopilot.Domain.Enums;
using Microsoft.Extensions.Options;

namespace InterviewCopilot.Application.Audio;

/// <summary>
/// Queues incoming frames for transcription without ever blocking the audio socket: each session gets a bounded
/// queue that drops its oldest frames when the worker falls behind. <see cref="TranscriptionWorker"/> drains it.
/// </summary>
public sealed class TranscriptionSink : IAudioFrameSink
{
    private readonly VadOptions _options;
    private readonly ConcurrentDictionary<Guid, SessionPipeline> _pipelines = new();
    private readonly Channel<SessionPipeline> _newPipelines = Channel.CreateUnbounded<SessionPipeline>(
        new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Creates the sink.</summary>
    /// <param name="options">Voice-activity settings (queue size and thresholds).</param>
    public TranscriptionSink(IOptions<VadOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    internal ChannelReader<SessionPipeline> NewPipelines => _newPipelines.Reader;

    /// <summary>Copies the frame into the session's queue and returns immediately.</summary>
    /// <param name="sessionId">The session the audio belongs to.</param>
    /// <param name="frame">One 100 ms frame; the sink copies it, so the caller may reuse its buffer.</param>
    /// <param name="cancellationToken">Unused: enqueueing never waits.</param>
    /// <returns>A completed task.</returns>
    public ValueTask OnFrameAsync(Guid sessionId, AudioFrame frame, CancellationToken cancellationToken)
    {
        var pipeline = PipelineFor(sessionId);
        pipeline.Frames.Writer.TryWrite(new AudioFrame(frame.Speaker, frame.Pcm16.ToArray()));
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Stops accepting frames for the session's pipeline and lets its worker drain what is queued, flush the
    /// speech still buffered and release the pipeline. A later stream for the same session starts a fresh one.
    /// </summary>
    /// <param name="sessionId">The session whose audio socket closed.</param>
    /// <param name="cancellationToken">Unused: completing the queue never waits.</param>
    /// <returns>A completed task.</returns>
    public ValueTask OnStreamEndedAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (_pipelines.TryGetValue(sessionId, out var pipeline))
        {
            pipeline.End();
        }

        return ValueTask.CompletedTask;
    }

    internal int PipelineCount => _pipelines.Count;

    internal long DroppedFrames(Guid sessionId) =>
        _pipelines.TryGetValue(sessionId, out var pipeline) ? pipeline.DroppedFrames : 0;

    /// <summary>Removes a drained pipeline. Only that exact instance goes, never a newer one for the same session.</summary>
    internal void Release(SessionPipeline pipeline) =>
        ((ICollection<KeyValuePair<Guid, SessionPipeline>>)_pipelines).Remove(new KeyValuePair<Guid, SessionPipeline>(pipeline.SessionId, pipeline));

    /// <summary>The live pipeline for the session; one that already ended is replaced so a reconnect never loses frames.</summary>
    private SessionPipeline PipelineFor(Guid sessionId)
    {
        while (true)
        {
            if (_pipelines.TryGetValue(sessionId, out var existing) && !existing.IsEnded)
            {
                return existing;
            }

            var created = new SessionPipeline(sessionId, _options);
            var added = existing is null
                ? _pipelines.TryAdd(sessionId, created)
                : _pipelines.TryUpdate(sessionId, created, existing);
            if (added)
            {
                _newPipelines.Writer.TryWrite(created);
                return created;
            }
        }
    }
}

/// <summary>One session's frame queue plus the per-speaker buffers that only its drain loop touches.</summary>
internal sealed class SessionPipeline
{
    private readonly Dictionary<Speaker, VoiceActivityBuffer> _buffers = [];
    private readonly VadOptions _options;
    private long _dropped;
    private long _reported;
    private bool _ended;

    public SessionPipeline(Guid sessionId, VadOptions options)
    {
        SessionId = sessionId;
        _options = options;
        Frames = Channel.CreateBounded(
            new BoundedChannelOptions(options.QueueFrames) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
            (AudioFrame _) => Interlocked.Increment(ref _dropped));
    }

    public Guid SessionId { get; }

    public Channel<AudioFrame> Frames { get; }

    public long DroppedFrames => Interlocked.Read(ref _dropped);

    /// <summary>True once the stream ended; the queue accepts no more frames.</summary>
    public bool IsEnded => Volatile.Read(ref _ended);

    /// <summary>Completes the queue so the drain loop finishes after the frames already in it.</summary>
    public void End()
    {
        Volatile.Write(ref _ended, true);
        Frames.Writer.TryComplete();
    }

    /// <summary>Ends every speaker's speech in progress. Call only from the drain loop, once the queue is empty.</summary>
    public List<(Speaker Speaker, SpeechSegment Segment)> FlushAll()
    {
        var result = new List<(Speaker, SpeechSegment)>();
        foreach (var (speaker, buffer) in _buffers)
        {
            if (buffer.Flush() is { } segment)
            {
                result.Add((speaker, segment));
            }
        }

        return result;
    }

    /// <summary>Drops since the last call, so each drop is reported once.</summary>
    public long TakeNewDrops()
    {
        var total = DroppedFrames;
        var fresh = total - _reported;
        _reported = total;
        return fresh;
    }

    public VoiceActivityBuffer BufferFor(Speaker speaker)
    {
        if (!_buffers.TryGetValue(speaker, out var buffer))
        {
            buffer = new VoiceActivityBuffer(_options);
            _buffers[speaker] = buffer;
        }

        return buffer;
    }
}
