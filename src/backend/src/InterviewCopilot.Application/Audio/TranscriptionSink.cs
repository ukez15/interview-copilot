using System.Collections.Concurrent;
using System.Threading.Channels;
using InterviewCopilot.Domain.Enums;
using Microsoft.Extensions.Options;

namespace InterviewCopilot.Application.Audio;

/// <summary>
/// Queues incoming frames for transcription without ever blocking the audio socket: each session gets a bounded
/// queue that drops its oldest frames when the worker falls behind. <see cref="TranscriptionWorker"/> drains it.
/// Every frame is numbered on arrival, so segment times stay true stream positions even when frames are dropped.
/// </summary>
public sealed class TranscriptionSink : IAudioFrameSink, IAudioStreamClock
{
    private readonly VadOptions _options;
    private readonly ISpeechToTextRouter _router;
    private readonly IAudioCaptureGate _gate;
    private readonly ConcurrentDictionary<Guid, SessionPipeline> _pipelines = new();
    private readonly ConcurrentDictionary<(Guid SessionId, Speaker Speaker), StreamCounter> _received = new();
    private readonly Channel<SessionPipeline> _newPipelines = Channel.CreateUnbounded<SessionPipeline>(
        new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Creates the sink.</summary>
    /// <param name="options">Voice-activity settings (queue size and thresholds).</param>
    /// <param name="router">Says which sessions go to Deepgram streaming instead; this sink skips those.</param>
    /// <param name="gate">Says which frames are worth transcribing (#78); none given means all of them.</param>
    public TranscriptionSink(IOptions<VadOptions> options, ISpeechToTextRouter router, IAudioCaptureGate? gate = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(router);
        _options = options.Value;
        _router = router;
        _gate = gate ?? OpenAudioCaptureGate.Instance;
    }

    internal ChannelReader<SessionPipeline> NewPipelines => _newPipelines.Reader;

    /// <inheritdoc />
    public long PositionMs(Guid sessionId, Speaker speaker) =>
        _received.TryGetValue((sessionId, speaker), out var counter) ? counter.Value * VadOptions.FrameMs : 0;

    /// <summary>
    /// Numbers the frame, then copies it into the session's queue and returns immediately. Deepgram-routed
    /// sessions and frames the gate turns away are skipped, but still counted, so the stream clock keeps time.
    /// </summary>
    /// <param name="sessionId">The session the audio belongs to.</param>
    /// <param name="frame">One 100 ms frame; the sink copies it, so the caller may reuse its buffer.</param>
    /// <param name="cancellationToken">Cancels the routing and gate lookups; enqueueing never waits.</param>
    /// <returns>A task that completes once the frame is queued (or skipped).</returns>
    public async ValueTask OnFrameAsync(Guid sessionId, AudioFrame frame, CancellationToken cancellationToken)
    {
        var index = _received.GetOrAdd((sessionId, frame.Speaker), _ => new StreamCounter()).Next();
        if (await _router.UsesDeepgramAsync(sessionId, cancellationToken)
            || !await _gate.AcceptsAsync(sessionId, frame.Speaker, cancellationToken))
        {
            return;
        }

        var pipeline = PipelineFor(sessionId);
        pipeline.Frames.Writer.TryWrite(new QueuedFrame(new AudioFrame(frame.Speaker, frame.Pcm16.ToArray()), index));
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

        // A reconnected stream restarts its clock at 0, as its segment times always have.
        foreach (var speaker in Enum.GetValues<Speaker>())
        {
            _received.TryRemove((sessionId, speaker), out _);
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

/// <summary>A queued frame and its position in the speaker's stream (0 for the first frame received).</summary>
internal readonly record struct QueuedFrame(AudioFrame Frame, long Index);

/// <summary>A thread-safe count of one speaker's frames; <see cref="Next"/> returns each frame's index.</summary>
internal sealed class StreamCounter
{
    private long _count;

    public long Value => Interlocked.Read(ref _count);

    public long Next() => Interlocked.Increment(ref _count) - 1;
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
            (QueuedFrame _) => Interlocked.Increment(ref _dropped));
    }

    public Guid SessionId { get; }

    public Channel<QueuedFrame> Frames { get; }

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
