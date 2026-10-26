using System.Globalization;
using System.Security.Cryptography;
using FluentAssertions;
using InterviewCopilot.Application.Audio;
using InterviewCopilot.Application.Common.Interfaces;
using InterviewCopilot.Application.Events;
using InterviewCopilot.Application.Providers;
using InterviewCopilot.Domain.Entities;
using InterviewCopilot.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InterviewCopilot.UnitTests.Audio;

public sealed class DeepgramStreamingSinkTests : IAsyncDisposable
{
    private static readonly DeepgramStreamingTimings FastTimings =
        new(TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(3));

    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly string _apiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    private readonly InMemoryStore _store = new();
    private readonly FakeDeepgramFactory _factory = new();
    private readonly InMemorySessionKeyStore _keys = new();
    private readonly EventLog _events = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly RecordingListener _listener = new();
    private readonly ThrowingUnitOfWork _unitOfWork;
    private readonly ServiceProvider _provider;
    private readonly FixedRouter _router = new(deepgram: true);
    private DeepgramStreamingSink? _sink;

    public DeepgramStreamingSinkTests()
    {
        _unitOfWork = new ThrowingUnitOfWork(_store);
        var services = new ServiceCollection();
        services.AddSingleton<ITranscriptRepository>(_store);
        services.AddSingleton<IUnitOfWork>(_unitOfWork);
        services.AddSingleton<ITranscriptSegmentListener>(new ThrowingListener());
        services.AddSingleton<ITranscriptSegmentListener>(_listener);
        _provider = services.BuildServiceProvider();
        _keys.Store(_sessionId, ProviderNames.Deepgram, _apiKey);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DeepgramStreamingSink Sink => _sink ??= Create(_router);

    public async ValueTask DisposeAsync()
    {
        if (_sink is not null)
        {
            await _sink.DisposeAsync();
        }

        await _provider.DisposeAsync();
        _logs.Dispose();
    }

    private DeepgramStreamingSink Create(ISpeechToTextRouter router) => new(
        _factory,
        router,
        _keys,
        _provider.GetRequiredService<IServiceScopeFactory>(),
        _events,
        new LoggerFactory([_logs]).CreateLogger<DeepgramStreamingSink>(),
        FastTimings);

    private static byte[] Pcm(byte fill) => Enumerable.Repeat(fill, 3_200).ToArray();

    private ValueTask SendAsync(Speaker speaker, byte fill, Guid? sessionId = null) =>
        Sink.OnFrameAsync(sessionId ?? _sessionId, new AudioFrame(speaker, Pcm(fill)), Ct);

    private static string Result(string text, double start = 0, double duration = 1, bool isFinal = true, string type = "Results") =>
        string.Create(
            CultureInfo.InvariantCulture,
            $$$"""{"type":"{{{type}}}","start":{{{start}}},"duration":{{{duration}}},"is_final":{{{(isFinal ? "true" : "false")}}},"channel":{"alternatives":[{"transcript":"{{{text}}}"}]}}""");

    private static async Task EventuallyAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10, Ct);
        }

        throw new TimeoutException($"Condition not met: {because}");
    }

    [Fact]
    public async Task Should_SendEachFrameAsAudio_OnOneConnectionPerSpeaker()
    {
        var interviewer = _factory.Enqueue();
        var candidate = _factory.Enqueue();

        await SendAsync(Speaker.Interviewer, 1);
        await SendAsync(Speaker.Interviewer, 2);
        await EventuallyAsync(() => interviewer.AudioFrames == 2, "interviewer frames");
        await SendAsync(Speaker.Candidate, 3);
        await EventuallyAsync(() => candidate.AudioFrames == 1, "candidate frame");

        _factory.ConnectAttempts.Should().Be(2);
        _factory.Keys.Should().OnlyContain(k => k == _apiKey);
        interviewer.Sent.Select(m => m.Payload[0]).Should().Equal(1, 2);
        interviewer.Sent.Should().OnlyContain(m => m.Kind == "audio" && m.Payload.Length == 3_200);
        candidate.Sent.Single().Payload[0].Should().Be(3);
    }

    [Fact]
    public async Task Should_SendKeepAlive_When_NoAudioForTheInterval()
    {
        var connection = _factory.Enqueue();

        await SendAsync(Speaker.Interviewer, 1);
        await EventuallyAsync(() => connection.KeepAlives >= 2, "two keep-alives");

        connection.AudioFrames.Should().Be(1);
    }

    [Fact]
    public async Task Should_NotSendKeepAlive_While_AudioKeepsFlowing()
    {
        var connection = _factory.Enqueue();

        for (var i = 0; i < 12; i++)
        {
            await SendAsync(Speaker.Interviewer, 1);
            await Task.Delay(25, Ct);
        }

        connection.KeepAlives.Should().Be(0);
    }

    [Fact]
    public async Task Should_CloseStreamDrainAndDispose_When_TheAudioStreamEnds()
    {
        var connection = _factory.Enqueue(new FakeDeepgramConnection { RepliesOnCloseStream = [Result("Last words", 0.1, 0.5)] });
        await SendAsync(Speaker.Interviewer, 1);
        await EventuallyAsync(() => connection.AudioFrames == 1, "frame sent");

        await Sink.OnStreamEndedAsync(_sessionId, Ct);

        connection.Sent[^1].Kind.Should().Be("close");
        connection.Disposed.Should().BeTrue();
        _store.Segments.Should().ContainSingle().Which.Text.Should().Be("Last words");
        _router.Forgotten.Should().Equal(_sessionId);
    }

    [Fact]
    public async Task Should_StopWaiting_When_DeepgramNeverFinishesTheDrain()
    {
        var sink = new DeepgramStreamingSink(
            _factory,
            _router,
            _keys,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _events,
            new LoggerFactory([_logs]).CreateLogger<DeepgramStreamingSink>(),
            new DeepgramStreamingTimings(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(100)));
        _sink = sink;
        var connection = _factory.Enqueue(new FakeDeepgramConnection { IgnoreCloseStream = true });
        await SendAsync(Speaker.Interviewer, 1);
        await EventuallyAsync(() => connection.AudioFrames == 1, "frame sent");

        await sink.OnStreamEndedAsync(_sessionId, Ct);

        connection.CloseStreamSent.Should().BeTrue();
        connection.Disposed.Should().BeTrue();
        _events.ErrorMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task Should_SaveAndPublishFinalResult_AndNotifyEveryListener()
    {
        var connection = _factory.Enqueue();
        await SendAsync(Speaker.Candidate, 1);
        await EventuallyAsync(() => connection.AudioFrames == 1, "frame sent");

        connection.Push(Result("I led the migration.", start: 1.25, duration: 2.5));
        var segment = await _listener.WaitAsync();

        segment.SessionId.Should().Be(_sessionId);
        segment.Speaker.Should().Be(Speaker.Candidate);
        segment.StartMs.Should().Be(1_250);
        segment.EndMs.Should().Be(3_750);
        segment.Text.Should().Be("I led the migration.");
        _store.Segments.Should().ContainSingle().Which.Should().BeSameAs(segment);
        _store.SaveCount.Should().Be(1);
        var published = _events.Events.Should().ContainSingle().Subject;
        published.SessionId.Should().Be(_sessionId);
        published.Type.Should().Be(SessionEventTypes.TranscriptSegment);
        var data = published.Data;
        data.GetType().GetProperty("id")!.GetValue(data).Should().Be(segment.Id);
        data.GetType().GetProperty("speaker")!.GetValue(data).Should().Be("Candidate");
        data.GetType().GetProperty("startMs")!.GetValue(data).Should().Be(1_250L);
        data.GetType().GetProperty("endMs")!.GetValue(data).Should().Be(3_750L);
        data.GetType().GetProperty("text")!.GetValue(data).Should().Be("I led the migration.");
    }

    [Fact]
    public async Task Should_IgnoreResultsThatAreNotFinalNonEmptyTranscripts()
    {
        var connection = _factory.Enqueue();
        await SendAsync(Speaker.Interviewer, 1);

        foreach (var message in new[]
        {
            Result("interim words", isFinal: false),
            Result("   "),
            Result("metadata", type: "Metadata"),
            """{"type":"Results","is_final":true,"channel":{"alternatives":[]}}""",
            """{"type":"Results","is_final":true,"channel":{"alternatives":[{"transcript":5}]}}""",
            """{"type":"Results","is_final":"yes","channel":{"alternatives":[{"transcript":"x"}]}}""",
            """["not","an","object"]""",
            "not json at all",
        })
        {
            connection.Push(message);
        }

        connection.Push(Result("Real one"));
        await _listener.WaitAsync();

        _store.Segments.Should().ContainSingle().Which.Text.Should().Be("Real one");
    }

    [Fact]
    public async Task Should_TreatMissingOrNegativeTimes_AsZero()
    {
        var connection = _factory.Enqueue();
        await SendAsync(Speaker.Interviewer, 1);

        connection.Push("""{"type":"Results","is_final":true,"start":"x","duration":-3,"channel":{"alternatives":[{"transcript":"Hello"}]}}""");
        var segment = await _listener.WaitAsync();

        segment.StartMs.Should().Be(0);
        segment.EndMs.Should().Be(0);
    }

    [Fact]
    public async Task Should_OffsetTimesByAudioAlreadySent_When_ReconnectingAfterADrop()
    {
        var first = _factory.Enqueue();
        var second = _factory.Enqueue();
        for (var i = 0; i < 3; i++)
        {
            await SendAsync(Speaker.Interviewer, 1);
        }

        await EventuallyAsync(() => first.AudioFrames == 3, "three frames on the first connection");
        first.Drop();
        await EventuallyAsync(() => _factory.ConnectAttempts == 2, "reconnect");
        await SendAsync(Speaker.Interviewer, 2);
        await EventuallyAsync(() => second.AudioFrames == 1, "frame on the second connection");
        second.Push(Result("After the drop", start: 0.2, duration: 0.4));
        var segment = await _listener.WaitAsync();

        segment.StartMs.Should().Be(500);
        segment.EndMs.Should().Be(900);
        _events.ErrorMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task Should_PublishNoKeyErrorOncePerSession_AndDropTheAudio()
    {
        var otherSession = Guid.NewGuid();
        var connection = _factory.Enqueue();

        await SendAsync(Speaker.Interviewer, 1, otherSession);
        await SendAsync(Speaker.Interviewer, 1, otherSession);
        await SendAsync(Speaker.Candidate, 1, otherSession);
        await SendAsync(Speaker.Interviewer, 1, _sessionId);

        await EventuallyAsync(() => connection.AudioFrames == 1, "keyed session streams");
        _events.ErrorMessages.Should().Equal(DeepgramStreamingSink.NoKeyMessage);
        _events.Events.Should().ContainSingle().Which.SessionId.Should().Be(otherSession);
        _factory.ConnectAttempts.Should().Be(1); // only the keyed session connected, and nobody fell back to Whisper
    }

    [Fact]
    public async Task Should_PublishTheReadableNoKeyMessage()
    {
        _keys.Clear(_sessionId);

        await SendAsync(Speaker.Interviewer, 1);

        _events.ErrorMessages.Should().ContainSingle().Which
            .Should().Be("Add your Deepgram API key in Settings, or switch speech-to-text back to Whisper.");
        _factory.ConnectAttempts.Should().Be(0);
    }

    [Fact]
    public async Task Should_StartStreaming_When_TheKeyArrivesAfterTheFirstFrames()
    {
        _keys.Clear(_sessionId);
        var connection = _factory.Enqueue();
        await SendAsync(Speaker.Interviewer, 1);

        _keys.Store(_sessionId, ProviderNames.Deepgram, _apiKey);
        await SendAsync(Speaker.Interviewer, 2);
        await EventuallyAsync(() => connection.AudioFrames == 1, "frame sent once the key exists");

        connection.Sent.Single().Payload[0].Should().Be(2);
        _events.ErrorMessages.Should().ContainSingle();
    }

    [Fact]
    public async Task Should_ReconnectOnce_AndKeepStreaming_When_TheSocketDrops()
    {
        var first = _factory.Enqueue();
        var second = _factory.Enqueue();
        await SendAsync(Speaker.Interviewer, 1);
        await EventuallyAsync(() => first.AudioFrames == 1, "frame sent");

        first.Drop();
        await EventuallyAsync(() => _factory.ConnectAttempts == 2, "reconnect");
        await SendAsync(Speaker.Interviewer, 2);
        await EventuallyAsync(() => second.AudioFrames == 1, "frame on the new connection");

        first.Disposed.Should().BeTrue();
        _events.ErrorMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task Should_PublishLostConnectionError_When_TheReconnectDropsToo()
    {
        var first = _factory.Enqueue();
        var second = _factory.Enqueue();
        await SendAsync(Speaker.Interviewer, 1);
        await EventuallyAsync(() => first.AudioFrames == 1, "frame sent");
        first.Drop();
        await EventuallyAsync(() => _factory.ConnectAttempts == 2, "reconnect");
        await SendAsync(Speaker.Interviewer, 2);
        await EventuallyAsync(() => second.AudioFrames == 1, "frame on the new connection");

        second.Drop();
        await EventuallyAsync(() => _events.ErrorMessages.Count == 1, "error event");
        await SendAsync(Speaker.Interviewer, 3);
        await SendAsync(Speaker.Interviewer, 4);
        await Task.Delay(100, Ct);

        _events.ErrorMessages.Should().Equal("Lost the connection to Deepgram.");
        _factory.ConnectAttempts.Should().Be(2);
        second.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task Should_PublishLostConnectionError_When_TheReconnectCannotConnect()
    {
        var first = _factory.Enqueue();
        _factory.EnqueueFailure(new DeepgramConnectionException("refused"));
        await SendAsync(Speaker.Interviewer, 1);
        await EventuallyAsync(() => first.AudioFrames == 1, "frame sent");

        first.Drop();
        await EventuallyAsync(() => _events.ErrorMessages.Count == 1, "error event");

        _events.ErrorMessages.Should().Equal(DeepgramStreamingSink.LostConnectionMessage);
        _factory.ConnectAttempts.Should().Be(2);
    }

    [Fact]
    public async Task Should_RetryTheFirstConnectOnce_And_PublishLostConnectionError_When_BothFail()
    {
        _factory.EnqueueFailure(new DeepgramConnectionException("refused"));
        _factory.EnqueueFailure(new DeepgramConnectionException("refused"));

        await SendAsync(Speaker.Interviewer, 1);
        await EventuallyAsync(() => _events.ErrorMessages.Count == 1, "error event");

        _events.ErrorMessages.Should().Equal("Lost the connection to Deepgram.");
        _factory.ConnectAttempts.Should().Be(2);
    }

    [Fact]
    public async Task Should_Recover_When_TheFirstConnectFailsButTheRetryWorks()
    {
        _factory.EnqueueFailure(new DeepgramConnectionException("refused"));
        var connection = _factory.Enqueue();

        await SendAsync(Speaker.Interviewer, 1);
        await EventuallyAsync(() => connection.AudioFrames == 1, "frame sent after the retry");

        _events.ErrorMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task Should_Reconnect_When_DeepgramClosesTheSocketOnItsOwn()
    {
        var first = _factory.Enqueue();
        var second = _factory.Enqueue();
        await SendAsync(Speaker.Interviewer, 1);
        await EventuallyAsync(() => first.AudioFrames == 1, "frame sent");

        first.CloseNormally();
        await EventuallyAsync(() => _factory.ConnectAttempts == 2, "reconnect");
        await SendAsync(Speaker.Interviewer, 2);
        await EventuallyAsync(() => second.AudioFrames == 1, "frame on the new connection");

        _events.ErrorMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task Should_PublishRejectedKeyError_WithoutRetrying_When_DeepgramAnswers401()
    {
        _factory.EnqueueFailure(new DeepgramRejectedKeyException());

        await SendAsync(Speaker.Interviewer, 1);
        await EventuallyAsync(() => _events.ErrorMessages.Count == 1, "error event");
        await SendAsync(Speaker.Interviewer, 2);
        await Task.Delay(100, Ct);

        _events.ErrorMessages.Should().Equal("Deepgram rejected the API key.");
        _factory.ConnectAttempts.Should().Be(1);
    }

    [Fact]
    public async Task Should_DoNothing_When_TheSessionIsNotRoutedToDeepgram()
    {
        _sink = Create(new FixedRouter(deepgram: false));
        _keys.Clear(_sessionId);

        await SendAsync(Speaker.Interviewer, 1);

        _factory.ConnectAttempts.Should().Be(0);
        _events.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task Should_OpenANewConnection_When_AudioArrivesAfterTheStreamEnded()
    {
        var first = _factory.Enqueue();
        var second = _factory.Enqueue();
        await SendAsync(Speaker.Interviewer, 1);
        await EventuallyAsync(() => first.AudioFrames == 1, "frame sent");
        await Sink.OnStreamEndedAsync(_sessionId, Ct);

        await SendAsync(Speaker.Interviewer, 2);
        await EventuallyAsync(() => second.AudioFrames == 1, "frame on the new connection");

        _factory.ConnectAttempts.Should().Be(2);
    }

    [Fact]
    public async Task Should_ForgetNoKeyError_When_TheStreamEnds()
    {
        _keys.Clear(_sessionId);
        await SendAsync(Speaker.Interviewer, 1);
        await Sink.OnStreamEndedAsync(_sessionId, Ct);

        await SendAsync(Speaker.Interviewer, 1);

        _events.ErrorMessages.Should().HaveCount(2);
    }

    [Fact]
    public async Task Should_ReportAGenericError_AndKeepStreaming_When_SavingAResultFails()
    {
        var connection = _factory.Enqueue();
        await SendAsync(Speaker.Interviewer, 1);
        _unitOfWork.FailNext = true;

        connection.Push(Result("Lost to a database error"));
        await EventuallyAsync(() => _events.ErrorMessages.Count == 1, "error event");
        connection.Push(Result("Saved fine"));
        await _listener.WaitAsync();

        _events.ErrorMessages.Should().Equal(TranscriptionWorker.GenericFailureMessage);
        _store.Segments.Select(s => s.Text).Should().Contain("Saved fine");
        _factory.ConnectAttempts.Should().Be(1);
    }

    [Fact]
    public async Task Should_NotWaitForTheNetwork_When_QueueingFrames()
    {
        var connection = _factory.Enqueue();

        for (var i = 0; i < 700; i++)
        {
            await SendAsync(Speaker.Interviewer, 1);
        }

        await EventuallyAsync(() => connection.AudioFrames > 0, "some frames sent");
        connection.AudioFrames.Should().BeLessThanOrEqualTo(700);
    }

    [Fact]
    public async Task Should_CloseEverySocket_When_TheSinkIsDisposed()
    {
        var connection = _factory.Enqueue();
        await SendAsync(Speaker.Interviewer, 1);
        await EventuallyAsync(() => connection.AudioFrames == 1, "frame sent");

        await Sink.DisposeAsync();

        connection.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task Should_NeverLogTheKeyOrTranscriptText()
    {
        var transcript = "secret-words-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        var first = _factory.Enqueue();
        var second = _factory.Enqueue();
        _factory.EnqueueFailure(new DeepgramConnectionException($"boom {_apiKey}"));
        await SendAsync(Speaker.Interviewer, 1);
        await EventuallyAsync(() => first.AudioFrames == 1, "frame sent");
        first.Push(Result(transcript));
        await _listener.WaitAsync();
        first.Drop();
        await EventuallyAsync(() => _factory.ConnectAttempts == 2, "reconnect");
        await SendAsync(Speaker.Candidate, 1);
        await EventuallyAsync(() => _factory.ConnectAttempts >= 3, "third connect (candidate)");
        second.Drop();
        await EventuallyAsync(() => _events.ErrorMessages.Count >= 1, "error event");
        _unitOfWork.FailNext = true;
        _keys.Clear(_sessionId);

        await Sink.OnStreamEndedAsync(_sessionId, Ct);

        _logs.Entries.Should().NotBeEmpty();
        _logs.Entries.Should().NotContain(e => e.Contains(_apiKey, StringComparison.Ordinal) || e.Contains(transcript, StringComparison.Ordinal));
        _events.ErrorMessages.Should().NotContain(m => m.Contains(_apiKey, StringComparison.Ordinal));
    }

    [Fact]
    public void Should_ThrowArgumentNull_When_ConstructorArgumentsMissing()
    {
        var scopes = _provider.GetRequiredService<IServiceScopeFactory>();
        var logger = new LoggerFactory().CreateLogger<DeepgramStreamingSink>();

        var cases = new Action[]
        {
            () => _ = new DeepgramStreamingSink(null!, _router, _keys, scopes, _events, logger),
            () => _ = new DeepgramStreamingSink(_factory, null!, _keys, scopes, _events, logger),
            () => _ = new DeepgramStreamingSink(_factory, _router, null!, scopes, _events, logger),
            () => _ = new DeepgramStreamingSink(_factory, _router, _keys, null!, _events, logger),
            () => _ = new DeepgramStreamingSink(_factory, _router, _keys, scopes, null!, logger),
            () => _ = new DeepgramStreamingSink(_factory, _router, _keys, scopes, _events, null!),
        };

        foreach (var create in cases)
        {
            create.Should().Throw<ArgumentNullException>();
        }
    }

    [Fact]
    public void Should_DefaultToEightSecondKeepAliveAndOneSecondReconnect()
    {
        DeepgramStreamingTimings.Default.KeepAlive.Should().Be(TimeSpan.FromSeconds(8));
        DeepgramStreamingTimings.Default.ReconnectDelay.Should().Be(TimeSpan.FromSeconds(1));
    }

    private sealed class ThrowingUnitOfWork(IUnitOfWork inner) : IUnitOfWork
    {
        public bool FailNext { get; set; }

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("database unavailable");
            }

            return inner.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class ThrowingListener : ITranscriptSegmentListener
    {
        public ValueTask OnSegmentAsync(TranscriptSegment segment, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("listener bug");
    }

    private sealed class RecordingListener : ITranscriptSegmentListener
    {
        private readonly System.Threading.Channels.Channel<TranscriptSegment> _segments =
            System.Threading.Channels.Channel.CreateUnbounded<TranscriptSegment>();

        public async Task<TranscriptSegment> WaitAsync() =>
            await _segments.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        public ValueTask OnSegmentAsync(TranscriptSegment segment, CancellationToken cancellationToken)
        {
            _segments.Writer.TryWrite(segment);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _entries = [];

        public IReadOnlyList<string> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
            // Nothing to release.
        }

        private void Add(string entry)
        {
            lock (_entries)
            {
                _entries.Add(entry);
            }
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Add(formatter(state, exception) + (exception is null ? string.Empty : " | " + exception));
        }
    }
}
