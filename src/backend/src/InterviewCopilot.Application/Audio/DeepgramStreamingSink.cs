using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Threading.Channels;
using InterviewCopilot.Application.Common.Interfaces;
using InterviewCopilot.Application.Events;
using InterviewCopilot.Application.Providers;
using InterviewCopilot.Domain.Entities;
using InterviewCopilot.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InterviewCopilot.Application.Audio;

/// <summary>Timings of the Deepgram socket (wave-3 brief, #20 Args). Tests shorten them.</summary>
/// <param name="KeepAlive">Send <c>KeepAlive</c> after this long without audio.</param>
/// <param name="ReconnectDelay">Wait this long before the one reconnect attempt.</param>
/// <param name="DrainTimeout">After <c>CloseStream</c>, wait at most this long for the remaining results.</param>
public sealed record DeepgramStreamingTimings(TimeSpan KeepAlive, TimeSpan ReconnectDelay, TimeSpan DrainTimeout)
{
    /// <summary>8 s keep-alive, 1 s reconnect delay, 10 s drain.</summary>
    public static DeepgramStreamingTimings Default { get; } =
        new(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
}

/// <summary>
/// Streams a Deepgram-routed session's audio to Deepgram (one socket per session and speaker) and turns its
/// final results into transcript segments, saved and published the same way <see cref="TranscriptionWorker"/>
/// does. Never falls back to Whisper: a missing key or a lost connection is reported as an <c>error</c> event.
/// </summary>
public sealed partial class DeepgramStreamingSink : IAudioFrameSink, IAsyncDisposable, IDisposable
{
    /// <summary>Sent once per session when Deepgram is chosen but no key was handed to the session.</summary>
    public const string NoKeyMessage = "Add your Deepgram API key in Settings, or switch speech-to-text back to Whisper.";

    /// <summary>Sent when the socket dropped and the one reconnect failed.</summary>
    public const string LostConnectionMessage = "Lost the connection to Deepgram.";

    /// <summary>Sent when Deepgram answers 401.</summary>
    public const string RejectedKeyMessage = "Deepgram rejected the API key.";

    /// <summary>Milliseconds of 16 kHz mono PCM16 audio per byte, inverted: bytes per millisecond.</summary>
    private const int BytesPerMs = AudioFrameParser.SampleRate * 2 / 1000;

    /// <summary>About 60 s of audio; beyond that the oldest frames are dropped rather than blocking the audio socket.</summary>
    private const int QueueFrames = 600;

    private readonly IDeepgramConnectionFactory _factory;
    private readonly ISpeechToTextRouter _router;
    private readonly ISessionKeyStore _keys;
    private readonly IServiceScopeFactory _scopes;
    private readonly ISessionEventPublisher _publisher;
    private readonly ILogger<DeepgramStreamingSink> _logger;
    private readonly DeepgramStreamingTimings _timings;
    private readonly ConcurrentDictionary<(Guid SessionId, Speaker Speaker), SpeakerStream> _streams = new();
    private readonly ConcurrentDictionary<Guid, byte> _noKeyReported = new();
    private readonly CancellationTokenSource _shutdown = new();
    private int _disposed;

    /// <summary>Creates the sink.</summary>
    /// <param name="factory">Opens Deepgram sockets.</param>
    /// <param name="router">Says which sessions are Deepgram-routed.</param>
    /// <param name="keys">Where the session's Deepgram key lives (in memory only).</param>
    /// <param name="scopes">Creates a scope per segment for the scoped repositories.</param>
    /// <param name="publisher">Where transcript and error events go.</param>
    /// <param name="logger">Logger; never receives the key, audio or transcript text.</param>
    /// <param name="timings">Keep-alive, reconnect and drain timings; defaults to <see cref="DeepgramStreamingTimings.Default"/>.</param>
    public DeepgramStreamingSink(
        IDeepgramConnectionFactory factory,
        ISpeechToTextRouter router,
        ISessionKeyStore keys,
        IServiceScopeFactory scopes,
        ISessionEventPublisher publisher,
        ILogger<DeepgramStreamingSink> logger,
        DeepgramStreamingTimings? timings = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(logger);
        _factory = factory;
        _router = router;
        _keys = keys;
        _scopes = scopes;
        _publisher = publisher;
        _logger = logger;
        _timings = timings ?? DeepgramStreamingTimings.Default;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deepgram connection for session {SessionId} ({Speaker}) failed ({ErrorType}).")]
    private partial void LogConnectionFailed(Guid sessionId, Speaker speaker, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deepgram stream for session {SessionId} ({Speaker}) gave up: {Reason}.")]
    private partial void LogStreamFailed(Guid sessionId, Speaker speaker, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Saving a Deepgram result failed for session {SessionId} ({ErrorType}).")]
    private partial void LogResultFailed(Guid sessionId, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A transcript listener failed for session {SessionId} ({ErrorType}).")]
    private partial void LogListenerFailed(Guid sessionId, string errorType);

    /// <summary>Queues the frame for the session's Deepgram socket; ignores sessions that aren't routed to Deepgram.</summary>
    /// <param name="sessionId">The session the audio belongs to.</param>
    /// <param name="frame">One 100 ms frame; the sink copies it.</param>
    /// <param name="cancellationToken">Cancels the routing lookup.</param>
    /// <returns>A task that completes once the frame is queued (never waits on the network).</returns>
    public async ValueTask OnFrameAsync(Guid sessionId, AudioFrame frame, CancellationToken cancellationToken)
    {
        if (!await _router.UsesDeepgramAsync(sessionId, cancellationToken))
        {
            return;
        }

        var slot = (sessionId, frame.Speaker);
        if (_streams.TryGetValue(slot, out var existing) && !existing.IsInputEnded)
        {
            existing.Enqueue(frame.Pcm16);
            return;
        }

        // The key may arrive after the first frames, so a missing key is re-checked on every frame.
        var apiKey = _keys.TryGet(sessionId, ProviderNames.Deepgram);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (_noKeyReported.TryAdd(sessionId, 0))
            {
                _publisher.Publish(sessionId, SessionEventTypes.Error, new { message = NoKeyMessage });
            }

            return;
        }

        var stream = new SpeakerStream(sessionId, frame.Speaker, apiKey);
        var added = existing is null ? _streams.TryAdd(slot, stream) : _streams.TryUpdate(slot, stream, existing);
        if (added)
        {
            stream.Run = RunAsync(stream);
            stream.Enqueue(frame.Pcm16);
        }
    }

    /// <summary>Sends <c>CloseStream</c> on the session's sockets, drains the remaining results and disposes them.</summary>
    /// <param name="sessionId">The session whose audio socket closed.</param>
    /// <param name="cancellationToken">Stops waiting for the drain.</param>
    /// <returns>A task that completes when every socket of the session is closed.</returns>
    public async ValueTask OnStreamEndedAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var ending = new List<SpeakerStream>();
        foreach (var key in _streams.Keys.Where(k => k.SessionId == sessionId))
        {
            if (_streams.TryRemove(key, out var stream))
            {
                stream.EndInput();
                ending.Add(stream);
            }
        }

        _noKeyReported.TryRemove(sessionId, out _);
        try
        {
            await Task.WhenAll(ending.Select(s => s.Run)).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The caller gave up waiting; the streams still finish on their own.
        }

        _router.Forget(sessionId);
    }

    /// <summary>Stops every socket without draining.</summary>
    /// <returns>A task that completes when all sockets are closed.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _shutdown.CancelAsync();
        var runs = _streams.Values.Select(s => s.Run).ToArray();
        _streams.Clear();
        await Task.WhenAll(runs);
        _shutdown.Dispose();
    }

    /// <summary>Stops every socket without draining (for containers that dispose synchronously).</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The stream runs detached; any failure must end in an error event, not an unobserved exception.")]
    private async Task RunAsync(SpeakerStream stream)
    {
        // Yield first so the caller can store the task before any work runs.
        await Task.Yield();
        var token = _shutdown.Token;
        var reconnected = false;
        try
        {
            while (true)
            {
                IDeepgramConnection connection;
                try
                {
                    connection = await _factory.ConnectAsync(stream.ApiKey, token);
                }
                catch (DeepgramRejectedKeyException)
                {
                    Fail(stream, RejectedKeyMessage, "key rejected");
                    return;
                }
                catch (DeepgramConnectionException ex)
                {
                    LogConnectionFailed(stream.SessionId, stream.Speaker, ex.GetType().Name);
                    if (reconnected)
                    {
                        Fail(stream, LostConnectionMessage, "could not reconnect");
                        return;
                    }

                    reconnected = true;
                    await Task.Delay(_timings.ReconnectDelay, token);
                    continue;
                }

                bool completed;
                await using (connection)
                {
                    completed = await PumpAsync(stream, connection, token);
                }

                if (completed)
                {
                    return;
                }

                if (reconnected)
                {
                    Fail(stream, LostConnectionMessage, "connection dropped again");
                    return;
                }

                reconnected = true;
                await Task.Delay(_timings.ReconnectDelay, token);
            }
        }
        catch (OperationCanceledException)
        {
            // The sink is shutting down.
        }
        catch (Exception ex)
        {
            LogConnectionFailed(stream.SessionId, stream.Speaker, ex.GetType().Name);
            Fail(stream, LostConnectionMessage, "unexpected failure");
        }
    }

    private void Fail(SpeakerStream stream, string message, string reason)
    {
        stream.MarkFailed();
        LogStreamFailed(stream.SessionId, stream.Speaker, reason);
        _publisher.Publish(stream.SessionId, SessionEventTypes.Error, new { message });
    }

    /// <summary>Runs one connection until the input ends (returns true) or the socket drops (returns false).</summary>
    private async Task<bool> PumpAsync(SpeakerStream stream, IDeepgramConnection connection, CancellationToken shutdown)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        var originMs = stream.ConsumedBytes / BytesPerMs;
        var receive = ReceiveLoopAsync(stream, connection, originMs, cts.Token);
        var send = SendLoopAsync(stream, connection, cts.Token);
        try
        {
            if (await Task.WhenAny(send, receive) == send)
            {
                await send;

                // CloseStream is sent: let Deepgram flush what it still holds, but not forever.
                cts.CancelAfter(_timings.DrainTimeout);
                try
                {
                    await receive;
                }
                catch (DeepgramConnectionException)
                {
                    // Nothing more to wait for: the session's audio has ended anyway.
                }
                catch (OperationCanceledException) when (!shutdown.IsCancellationRequested)
                {
                    // Drain timeout.
                }

                return true;
            }

            await receive;
            return false;
        }
        catch (DeepgramConnectionException ex)
        {
            LogConnectionFailed(stream.SessionId, stream.Speaker, ex.GetType().Name);
            return false;
        }
        finally
        {
            await cts.CancelAsync();
            await ObserveAsync(send);
            await ObserveAsync(receive);
        }
    }

    /// <summary>Waits for a loop to finish without rethrowing, and marks its exception (already handled by the caller) as observed.</summary>
    private static async Task ObserveAsync(Task task)
    {
        await Task.WhenAny(task);
        _ = task.Exception;
    }

    private async Task SendLoopAsync(SpeakerStream stream, IDeepgramConnection connection, CancellationToken cancellationToken)
    {
        var reader = stream.Reader;
        while (true)
        {
            if (reader.TryRead(out var pcm))
            {
                await connection.SendAudioAsync(pcm, cancellationToken);
                stream.AddConsumed(pcm.Length);
                continue;
            }

            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            idle.CancelAfter(_timings.KeepAlive);
            try
            {
                if (!await reader.WaitToReadAsync(idle.Token))
                {
                    break;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await connection.SendKeepAliveAsync(cancellationToken);
            }
        }

        await connection.SendCloseStreamAsync(cancellationToken);
    }

    private async Task ReceiveLoopAsync(SpeakerStream stream, IDeepgramConnection connection, long originMs, CancellationToken cancellationToken)
    {
        while (await connection.ReceiveAsync(cancellationToken) is { } message)
        {
            await HandleMessageAsync(stream, message, originMs, cancellationToken);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "One bad result must not stop the session's transcription; it is reported as an error event.")]
    private async Task HandleMessageAsync(SpeakerStream stream, string message, long originMs, CancellationToken cancellationToken)
    {
        if (!TryParseFinalResult(message, out var text, out var startSeconds, out var durationSeconds))
        {
            return;
        }

        var startMs = originMs + ToMilliseconds(startSeconds);
        var endMs = startMs + ToMilliseconds(durationSeconds);
        try
        {
            using var scope = _scopes.CreateScope();
            var segment = TranscriptSegment.Create(stream.SessionId, stream.Speaker, startMs, endMs, text);
            scope.ServiceProvider.GetRequiredService<ITranscriptRepository>().AddSegment(segment);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(cancellationToken);

            _publisher.Publish(
                stream.SessionId,
                SessionEventTypes.TranscriptSegment,
                new { id = segment.Id, speaker = stream.Speaker.ToString(), startMs = segment.StartMs, endMs = segment.EndMs, text = segment.Text });

            await NotifyListenersAsync(scope.ServiceProvider, stream.SessionId, segment, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogResultFailed(stream.SessionId, ex.GetType().Name);
            _publisher.Publish(stream.SessionId, SessionEventTypes.Error, new { message = TranscriptionWorker.GenericFailureMessage });
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Listener failures are isolated and logged.")]
    private async Task NotifyListenersAsync(IServiceProvider services, Guid sessionId, TranscriptSegment segment, CancellationToken cancellationToken)
    {
        foreach (var listener in services.GetServices<ITranscriptSegmentListener>())
        {
            try
            {
                await listener.OnSegmentAsync(segment, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogListenerFailed(sessionId, ex.GetType().Name);
            }
        }
    }

    private static long ToMilliseconds(double seconds) =>
        double.IsFinite(seconds) && seconds > 0 ? (long)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero) : 0;

    /// <summary>Reads a <c>Results</c> message; only final, non-empty transcripts qualify.</summary>
    private static bool TryParseFinalResult(string message, out string text, out double startSeconds, out double durationSeconds)
    {
        text = string.Empty;
        startSeconds = 0;
        durationSeconds = 0;
        try
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "Results"
                || !root.TryGetProperty("is_final", out var isFinal) || isFinal.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("channel", out var channel) || channel.ValueKind != JsonValueKind.Object
                || !channel.TryGetProperty("alternatives", out var alternatives) || alternatives.ValueKind != JsonValueKind.Array
                || alternatives.GetArrayLength() == 0
                || !alternatives[0].TryGetProperty("transcript", out var transcript) || transcript.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var value = transcript.GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            text = value;
            startSeconds = ReadNumber(root, "start");
            durationSeconds = ReadNumber(root, "duration");
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static double ReadNumber(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : 0;

    /// <summary>One speaker's audio queue and socket state within one session.</summary>
    private sealed class SpeakerStream
    {
        private readonly Channel<byte[]> _frames = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(QueueFrames) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

        private long _consumedBytes;
        private bool _inputEnded;

        public SpeakerStream(Guid sessionId, Speaker speaker, string apiKey)
        {
            SessionId = sessionId;
            Speaker = speaker;
            ApiKey = apiKey;
        }

        public Guid SessionId { get; }

        public Speaker Speaker { get; }

        public string ApiKey { get; }

        public Task Run { get; set; } = Task.CompletedTask;

        public ChannelReader<byte[]> Reader => _frames.Reader;

        /// <summary>Audio bytes already sent: the position of the next frame within this speaker's stream.</summary>
        public long ConsumedBytes => Interlocked.Read(ref _consumedBytes);

        /// <summary>True once the stream ended (normally or because it failed): the queue takes no more audio.</summary>
        public bool IsInputEnded => Volatile.Read(ref _inputEnded);

        public void Enqueue(ReadOnlyMemory<byte> pcm16) => _frames.Writer.TryWrite(pcm16.ToArray());

        public void AddConsumed(int bytes) => Interlocked.Add(ref _consumedBytes, bytes);

        public void EndInput()
        {
            Volatile.Write(ref _inputEnded, true);
            _frames.Writer.TryComplete();
        }

        /// <summary>The stream gave up: later audio is dropped (the slot stays occupied until the session's stream ends).</summary>
        public void MarkFailed() => _frames.Writer.TryComplete();
    }
}
