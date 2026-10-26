using FluentAssertions;
using InterviewCopilot.Application.Audio;
using InterviewCopilot.Domain.Enums;
using Microsoft.Extensions.Options;

namespace InterviewCopilot.UnitTests.Audio;

public sealed class TranscriptionSinkGateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AudioFrame Frame(Speaker speaker) => new(speaker, new byte[3_200]);

    private static TranscriptionSink Sink(IAudioCaptureGate? gate = null, bool deepgram = false) =>
        new(Options.Create(new VadOptions()), new FixedRouter(deepgram), gate);

    private static async Task SendAsync(TranscriptionSink sink, Guid sessionId, Speaker speaker, int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            await sink.OnFrameAsync(sessionId, Frame(speaker), Ct);
        }
    }

    [Fact]
    public async Task Should_KeepAStreamClockPerSessionAndSpeaker()
    {
        var sink = Sink();
        var session = Guid.NewGuid();

        await SendAsync(sink, session, Speaker.Candidate, 3);
        await SendAsync(sink, session, Speaker.Interviewer, 1);

        sink.PositionMs(session, Speaker.Candidate).Should().Be(300);
        sink.PositionMs(session, Speaker.Interviewer).Should().Be(100);
        sink.PositionMs(Guid.NewGuid(), Speaker.Candidate).Should().Be(0);
    }

    [Fact]
    public async Task Should_DropTheFrame_ButKeepTheClockRunning_When_TheGateRefuses()
    {
        var sink = Sink(new SwitchGate { Accept = false });
        var session = Guid.NewGuid();

        await SendAsync(sink, session, Speaker.Candidate, 2);

        sink.PipelineCount.Should().Be(0);
        sink.PositionMs(session, Speaker.Candidate).Should().Be(200);
    }

    [Fact]
    public async Task Should_StampQueuedFramesWithTheirStreamIndex()
    {
        var gate = new SwitchGate { Accept = false };
        var sink = Sink(gate);
        var session = Guid.NewGuid();
        await SendAsync(sink, session, Speaker.Candidate, 2);

        gate.Accept = true;
        await SendAsync(sink, session, Speaker.Candidate, 1);

        sink.NewPipelines.TryRead(out var pipeline).Should().BeTrue();
        pipeline!.Frames.Reader.TryRead(out var queued).Should().BeTrue();
        queued.Index.Should().Be(2);
        queued.Frame.Speaker.Should().Be(Speaker.Candidate);
    }

    [Fact]
    public async Task Should_CountDeepgramSessionsOnTheClock()
    {
        var sink = Sink(deepgram: true);
        var session = Guid.NewGuid();

        await SendAsync(sink, session, Speaker.Candidate, 1);

        sink.PipelineCount.Should().Be(0);
        sink.PositionMs(session, Speaker.Candidate).Should().Be(100);
    }
}
