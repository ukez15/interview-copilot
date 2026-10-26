using FluentAssertions;
using InterviewCopilot.Application.Audio;
using InterviewCopilot.Domain.Enums;
using Microsoft.Extensions.Options;

namespace InterviewCopilot.UnitTests.Audio;

public sealed class TranscriptionSinkRoutingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AudioFrame Frame() => new(Speaker.Interviewer, new byte[3_200]);

    [Fact]
    public async Task Should_SkipTheSession_When_ItIsRoutedToDeepgram()
    {
        var sink = new TranscriptionSink(Options.Create(new VadOptions()), new FixedRouter(deepgram: true));

        await sink.OnFrameAsync(Guid.NewGuid(), Frame(), Ct);

        sink.PipelineCount.Should().Be(0);
        sink.NewPipelines.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task Should_QueueTheFrame_When_ItIsRoutedToWhisper()
    {
        var sink = new TranscriptionSink(Options.Create(new VadOptions()), new FixedRouter(deepgram: false));

        await sink.OnFrameAsync(Guid.NewGuid(), Frame(), Ct);

        sink.PipelineCount.Should().Be(1);
    }

    [Fact]
    public async Task Should_KeepEachSessionOnItsOwnPath_When_OnlyOneIsRoutedToDeepgram()
    {
        var deepgramSession = Guid.NewGuid();
        var whisperSession = Guid.NewGuid();
        var sink = new TranscriptionSink(Options.Create(new VadOptions()), new PerSessionRouter(deepgramSession));

        await sink.OnFrameAsync(deepgramSession, Frame(), Ct);
        await sink.OnFrameAsync(whisperSession, Frame(), Ct);

        sink.PipelineCount.Should().Be(1);
        sink.NewPipelines.TryRead(out var pipeline).Should().BeTrue();
        pipeline!.SessionId.Should().Be(whisperSession);
    }

    private sealed class PerSessionRouter(Guid deepgramSession) : ISpeechToTextRouter
    {
        public ValueTask<bool> UsesDeepgramAsync(Guid sessionId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(sessionId == deepgramSession);

        public void Forget(Guid sessionId)
        {
            // Not needed by the sink under test.
        }
    }
}
