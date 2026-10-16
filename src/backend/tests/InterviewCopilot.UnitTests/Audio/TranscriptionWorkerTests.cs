using System.Buffers.Binary;
using FluentAssertions;
using InterviewCopilot.Application.Audio;
using InterviewCopilot.Application.Common.Interfaces;
using InterviewCopilot.Application.Events;
using InterviewCopilot.Application.Providers;
using InterviewCopilot.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InterviewCopilot.UnitTests.Audio;

public sealed class TranscriptionWorkerTests : IAsyncDisposable
{
    private const int FrameBytes = 3_200;

    private readonly InMemoryStore _store = new();
    private readonly FakeStt _stt = new();
    private readonly RecordingPublisher _publisher = new();
    private readonly TranscriptionSink _sink;
    private readonly ServiceProvider _provider;
    private readonly TranscriptionWorker _worker;
    private readonly Guid _sessionId = Guid.NewGuid();

    public TranscriptionWorkerTests()
    {
        _sink = new TranscriptionSink(Options.Create(new VadOptions()));
        var services = new ServiceCollection();
        services.AddSingleton<ITranscriptRepository>(_store);
        services.AddSingleton<IUnitOfWork>(_store);
        services.AddSingleton<ISpeechToTextProvider>(_stt);
        _provider = services.BuildServiceProvider();
        _worker = new TranscriptionWorker(
            _sink,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _publisher,
            NullLogger<TranscriptionWorker>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        await _worker.StopAsync(CancellationToken.None);
        _worker.Dispose();
        await _provider.DisposeAsync();
    }

    private static byte[] Tone(short amplitude)
    {
        var bytes = new byte[FrameBytes];
        for (var i = 0; i < bytes.Length; i += 2)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i), amplitude);
        }

        return bytes;
    }

    private async Task SendAsync(Speaker speaker, int loudFrames, int silentFrames)
    {
        for (var i = 0; i < loudFrames; i++)
        {
            await _sink.OnFrameAsync(_sessionId, new AudioFrame(speaker, Tone(8_000)), Ct);
        }

        for (var i = 0; i < silentFrames; i++)
        {
            await _sink.OnFrameAsync(_sessionId, new AudioFrame(speaker, Tone(0)), Ct);
        }
    }

    [Fact]
    public async Task Should_SaveAndPublishSegment_When_SpeakerPauses()
    {
        _stt.Reply = "Tell me about yourself";
        await _worker.StartAsync(Ct);

        await SendAsync(Speaker.Interviewer, loudFrames: 5, silentFrames: 6);
        var published = await _publisher.WaitForAsync(1);

        var segment = _store.Segments.Should().ContainSingle().Subject;
        segment.SessionId.Should().Be(_sessionId);
        segment.Speaker.Should().Be(Speaker.Interviewer);
        segment.StartMs.Should().Be(0);
        segment.EndMs.Should().Be(500);
        segment.Text.Should().Be("Tell me about yourself");
        _store.SaveCount.Should().Be(1);
        published[0].SessionId.Should().Be(_sessionId);
        published[0].Type.Should().Be(SessionEventTypes.TranscriptSegment);
        var data = published[0].Data;
        data.GetType().GetProperty("id")!.GetValue(data).Should().Be(segment.Id);
        data.GetType().GetProperty("speaker")!.GetValue(data).Should().Be("Interviewer");
        data.GetType().GetProperty("startMs")!.GetValue(data).Should().Be(0L);
        data.GetType().GetProperty("endMs")!.GetValue(data).Should().Be(500L);
        data.GetType().GetProperty("text")!.GetValue(data).Should().Be("Tell me about yourself");
    }

    [Fact]
    public async Task Should_SendChunkToProvider_WithSpeakerSampleRateAndStart()
    {
        await _worker.StartAsync(Ct);

        await SendAsync(Speaker.Candidate, loudFrames: 0, silentFrames: 3);
        await SendAsync(Speaker.Candidate, loudFrames: 4, silentFrames: 6);
        await _publisher.WaitForAsync(1);

        var chunk = _stt.Chunks.Should().ContainSingle().Subject;
        chunk.Speaker.Should().Be(Speaker.Candidate);
        chunk.SampleRate.Should().Be(16_000);
        chunk.StartMs.Should().Be(300);
        chunk.Pcm16.Length.Should().Be(4 * FrameBytes);
    }

    [Fact]
    public async Task Should_EmitNothing_When_OnlySilence()
    {
        await _worker.StartAsync(Ct);

        await SendAsync(Speaker.Interviewer, loudFrames: 0, silentFrames: 600);
        await SendAsync(Speaker.Candidate, loudFrames: 5, silentFrames: 6);
        await _publisher.WaitForAsync(1);

        _stt.Chunks.Should().ContainSingle().Which.Speaker.Should().Be(Speaker.Candidate);
    }

    [Fact]
    public async Task Should_EmitNothing_When_ProviderReturnsEmptyText()
    {
        _stt.Reply = "  ";
        await _worker.StartAsync(Ct);

        await SendAsync(Speaker.Interviewer, loudFrames: 5, silentFrames: 6);
        await _stt.WaitForCallsAsync(1);
        await SendAsync(Speaker.Interviewer, loudFrames: 5, silentFrames: 6);
        await _stt.WaitForCallsAsync(1);

        _store.Segments.Should().BeEmpty();
        _publisher.Events.Should().BeEmpty();
        _store.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task Should_KeepSpeakersIndependent_When_TheyOverlap()
    {
        _stt.ReplyFor = chunk => chunk.Speaker == Speaker.Interviewer ? "question" : "answer";
        await _worker.StartAsync(Ct);

        for (var frame = 0; frame < 30; frame++)
        {
            var interviewerLoud = frame < 5;
            var candidateLoud = frame is >= 3 and < 10;
            await _sink.OnFrameAsync(_sessionId, new AudioFrame(Speaker.Interviewer, Tone(interviewerLoud ? (short)8_000 : (short)0)), Ct);
            await _sink.OnFrameAsync(_sessionId, new AudioFrame(Speaker.Candidate, Tone(candidateLoud ? (short)8_000 : (short)0)), Ct);
        }

        await _publisher.WaitForAsync(2);

        var ordered = _store.Segments.OrderBy(s => s.StartMs).ToList();
        ordered.Select(s => (s.Speaker, s.StartMs, s.EndMs)).Should().Equal(
            (Speaker.Interviewer, 0L, 500L),
            (Speaker.Candidate, 300L, 1_000L));
    }

    [Fact]
    public async Task Should_PublishGenericErrorAndKeepGoing_When_ProviderThrowsUnexpectedly()
    {
        _stt.FailNext = new InvalidOperationException(@"C:\secret\path\model.bin missing");
        await _worker.StartAsync(Ct);

        await SendAsync(Speaker.Interviewer, loudFrames: 5, silentFrames: 6);
        await SendAsync(Speaker.Interviewer, loudFrames: 5, silentFrames: 6);
        var published = await _publisher.WaitForAsync(2);

        published[0].Type.Should().Be(SessionEventTypes.Error);
        MessageOf(published[0]).Should().Be(TranscriptionWorker.GenericFailureMessage, "internal details must not reach clients");
        published[1].Type.Should().Be(SessionEventTypes.TranscriptSegment);
        _store.Segments.Should().ContainSingle();
    }

    [Fact]
    public async Task Should_PassThroughUserFacingProviderMessage()
    {
        _stt.FailNext = new TranscriptionUnavailableException("Add your Deepgram API key in Settings → Providers.");
        await _worker.StartAsync(Ct);

        await SendAsync(Speaker.Interviewer, loudFrames: 5, silentFrames: 6);
        var published = await _publisher.WaitForAsync(1);

        MessageOf(published[0]).Should().Be("Add your Deepgram API key in Settings → Providers.");
    }

    private static string? MessageOf(Published published) =>
        published.Data.GetType().GetProperty("message")!.GetValue(published.Data) as string;

    [Fact]
    public async Task Should_NotBlockSender_And_DropOldest_When_QueueIsFull()
    {
        var sessionId = Guid.NewGuid();

        for (var i = 0; i < 250; i++)
        {
            await _sink.OnFrameAsync(sessionId, new AudioFrame(Speaker.Interviewer, Tone(8_000)), Ct);
        }

        _sink.DroppedFrames(sessionId).Should().Be(50);
        _sink.DroppedFrames(Guid.NewGuid()).Should().Be(0);
    }

    [Fact]
    public async Task Should_CopyFrame_When_CallerReusesBuffer()
    {
        _stt.Reply = "ok";
        var shared = Tone(8_000);
        await _worker.StartAsync(Ct);

        for (var i = 0; i < 5; i++)
        {
            await _sink.OnFrameAsync(_sessionId, new AudioFrame(Speaker.Interviewer, shared), Ct);
        }

        Array.Clear(shared);
        await SendAsync(Speaker.Interviewer, loudFrames: 0, silentFrames: 6);
        await _publisher.WaitForAsync(1);

        _stt.Chunks.Single().Pcm16.Span.ToArray().Should().Contain(b => b != 0);
    }

    [Fact]
    public async Task Should_StopCleanly_When_HostShutsDownWhileIdle()
    {
        await _worker.StartAsync(Ct);
        await SendAsync(Speaker.Interviewer, loudFrames: 1, silentFrames: 0);

        var stop = () => _worker.StopAsync(CancellationToken.None);

        await stop.Should().NotThrowAsync();
    }

    [Fact]
    public void Should_ThrowArgumentNull_When_ConstructorArgumentsMissing()
    {
        var sink = () => new TranscriptionSink(null!);
        var worker = () => new TranscriptionWorker(null!, _provider.GetRequiredService<IServiceScopeFactory>(), _publisher, NullLogger<TranscriptionWorker>.Instance);

        sink.Should().Throw<ArgumentNullException>();
        worker.Should().Throw<ArgumentNullException>();
    }

    private sealed class FakeStt : ISpeechToTextProvider, IDisposable
    {
        private readonly List<AudioChunk> _chunks = [];
        private readonly SemaphoreSlim _calls = new(0);

        public string Name => "fake";

        public void Dispose() => _calls.Dispose();

        public string Reply { get; set; } = "hello";

        public Func<AudioChunk, string>? ReplyFor { get; set; }

        /// <summary>Thrown once by the next call, then cleared.</summary>
        public Exception? FailNext { get; set; }

        public IReadOnlyList<AudioChunk> Chunks
        {
            get
            {
                lock (_chunks)
                {
                    return [.. _chunks];
                }
            }
        }

        public Task<TranscriptionResult> TranscribeAsync(AudioChunk chunk, string? apiKey, CancellationToken cancellationToken)
        {
            lock (_chunks)
            {
                _chunks.Add(chunk);
            }

            _calls.Release();
            if (FailNext is { } failure)
            {
                FailNext = null;
                throw failure;
            }

            return Task.FromResult(new TranscriptionResult(ReplyFor?.Invoke(chunk) ?? Reply));
        }

        public async Task WaitForCallsAsync(int count)
        {
            for (var i = 0; i < count; i++)
            {
                (await _calls.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Should().BeTrue();
            }

            await Task.Delay(100, TestContext.Current.CancellationToken); // let the worker finish handling the result
        }
    }

    private sealed record Published(Guid SessionId, string Type, object Data);

    private sealed class RecordingPublisher : ISessionEventPublisher
    {
        private readonly List<Published> _events = [];

        public IReadOnlyList<Published> Events
        {
            get
            {
                lock (_events)
                {
                    return [.. _events];
                }
            }
        }

        public void Publish(Guid sessionId, string type, object data)
        {
            lock (_events)
            {
                _events.Add(new Published(sessionId, type, data));
            }
        }

        public async Task<IReadOnlyList<Published>> WaitForAsync(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (Events.Count >= count)
                {
                    return Events;
                }

                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            throw new TimeoutException($"Expected {count} events, got {Events.Count}.");
        }
    }
}
