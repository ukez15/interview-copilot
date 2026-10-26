using FluentAssertions;
using InterviewCopilot.Application.Audio;
using InterviewCopilot.Application.Common.Exceptions;
using InterviewCopilot.Application.Sessions;
using InterviewCopilot.Domain.Entities;
using InterviewCopilot.Domain.Enums;

namespace InterviewCopilot.UnitTests.Audio;

public class AudioIngestTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Message(byte speaker, int pcmBytes) => [speaker, .. new byte[pcmBytes]];

    [Theory]
    [InlineData(0, Speaker.Interviewer)]
    [InlineData(1, Speaker.Candidate)]
    public void Should_ParseFrame_When_SpeakerAndLengthValid(byte speaker, Speaker expected)
    {
        AudioFrameParser.TryParse(Message(speaker, 3_200), out var frame).Should().BeTrue();

        frame.Speaker.Should().Be(expected);
        frame.Pcm16.Length.Should().Be(3_200);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(0, 3_201)]
    [InlineData(0, 3_202)]
    [InlineData(2, 100)]
    public void Should_RejectFrame_When_Malformed(byte speaker, int pcmBytes)
    {
        AudioFrameParser.TryParse(Message(speaker, pcmBytes), out _).Should().BeFalse();
    }

    [Fact]
    public void Should_RejectEmptyMessage()
    {
        AudioFrameParser.TryParse(ReadOnlyMemory<byte>.Empty, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Should_CountBytesPerSpeaker()
    {
        var stats = new AudioStatsSink(new FixedClock());
        var session = Guid.NewGuid();

        await stats.OnFrameAsync(session, new AudioFrame(Speaker.Interviewer, new byte[3_200]), Ct);
        await stats.OnFrameAsync(session, new AudioFrame(Speaker.Candidate, new byte[100]), Ct);

        stats.Get(session).Should().Be(new AudioStatsDto(3_200, 100, 2, FixedClock.Default));
        stats.Get(Guid.NewGuid()).Should().Be(new AudioStatsDto(0, 0, 0, null));
    }

    [Fact]
    public async Task Should_AllowOnlyLiveSessionsOfTheOwner_And_FanOutToSinks()
    {
        var store = new InMemoryStore();
        var owner = Guid.NewGuid();
        var session = Session.Create(owner, "Live", SessionMode.LiveInterview, FixedClock.Default);
        store.Add(session);
        var stats = new AudioStatsSink(new FixedClock());
        var ingest = new AudioIngestService(new SessionService(store, store, new FixedClock()), [stats]);

        await ((Func<Task>)(() => ingest.AuthorizeAsync(owner, session.Id, Ct))).Should().ThrowAsync<ConflictException>();
        await ((Func<Task>)(() => ingest.AuthorizeAsync(Guid.NewGuid(), session.Id, Ct))).Should().ThrowAsync<NotFoundException>();

        session.Start(FixedClock.Default);
        await ingest.AuthorizeAsync(owner, session.Id, Ct);
        await ingest.IngestAsync(session.Id, new AudioFrame(Speaker.Candidate, new byte[10]), Ct);

        stats.Get(session.Id).CandidateBytes.Should().Be(10);
        new ConflictException("x", new InvalidOperationException()).InnerException.Should().NotBeNull();
        new ConflictException().Should().NotBeNull();
    }
}
