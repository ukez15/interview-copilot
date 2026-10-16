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
        if (!_pipelines.TryGetValue(sessionId, out var pipeline))
        {
            var created = new SessionPipeline(sessionId, _options);
            pipeline = _pipelines.GetOrAdd(sessionId, created);
            if (ReferenceEquals(pipeline, created))
            {
                _newPipelines.Writer.TryWrite(created);
            }
        }

        pipeline.Frames.Writer.TryWrite(new AudioFrame(frame.Speaker, frame.Pcm16.ToArray()));
        return ValueTask.CompletedTask;
    }

    internal long DroppedFrames(Guid sessionId) =>
        _pipelines.TryGetValue(sessionId, out var pipeline) ? pipeline.DroppedFrames : 0;
}

/// <summary>One session's frame queue plus the per-speaker buffers that only its drain loop touches.</summary>
internal sealed class SessionPipeline
{
    private readonly Dictionary<Speaker, VoiceActivityBuffer> _buffers = [];
    private readonly VadOptions _options;
    private long _dropped;
    private long _reported;

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
