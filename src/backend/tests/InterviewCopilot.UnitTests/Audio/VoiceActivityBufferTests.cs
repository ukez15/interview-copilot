using System.Buffers.Binary;
using FluentAssertions;
using InterviewCopilot.Application.Audio;

namespace InterviewCopilot.UnitTests.Audio;

public class VoiceActivityBufferTests
{
    private const int FrameBytes = 3_200;

    private static byte[] Tone(short amplitude)
    {
        var bytes = new byte[FrameBytes];
        for (var i = 0; i < bytes.Length; i += 2)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i), amplitude);
        }

        return bytes;
    }

    private static byte[] Loud() => Tone(8_000); // about -12 dBFS

    private static byte[] Silent() => new byte[FrameBytes];

    private static List<SpeechSegment> Feed(VoiceActivityBuffer buffer, params (byte[] Frame, int Count)[] script)
    {
        var segments = new List<SpeechSegment>();
        foreach (var (frame, count) in script)
        {
            for (var i = 0; i < count; i++)
            {
                var segment = buffer.Push(frame);
                if (segment is not null)
                {
                    segments.Add(segment);
                }
            }
        }

        return segments;
    }

    private static VoiceActivityBuffer NewBuffer() => new(new VadOptions());

    [Fact]
    public void Should_EmitNothing_When_TenMinutesOfSilence()
    {
        var segments = Feed(NewBuffer(), (Silent(), 6_000));

        segments.Should().BeEmpty();
    }

    [Fact]
    public void Should_EmitNothing_When_AudioIsBelowThreshold()
    {
        var segments = Feed(NewBuffer(), (Tone(100), 100)); // about -50 dBFS

        segments.Should().BeEmpty();
    }

    [Fact]
    public void Should_TreatAudioAboveThresholdAsSpeech()
    {
        var segments = Feed(NewBuffer(), (Tone(400), 5), (Silent(), 6)); // about -38 dBFS

        segments.Should().ContainSingle();
    }

    [Fact]
    public void Should_EndSegment_When_PauseReaches600Ms()
    {
        var buffer = NewBuffer();
        Feed(buffer, (Loud(), 5)).Should().BeEmpty();
        Feed(buffer, (Silent(), 5)).Should().BeEmpty();

        var segment = buffer.Push(Silent());

        segment.Should().NotBeNull();
        segment.StartMs.Should().Be(0);
        segment.EndMs.Should().Be(500);
        segment.Pcm16.Length.Should().Be(5 * FrameBytes);
    }

    [Fact]
    public void Should_KeepOneSegment_When_PauseIsShorterThan600Ms()
    {
        var segments = Feed(NewBuffer(), (Loud(), 3), (Silent(), 5), (Loud(), 3), (Silent(), 6));

        var segment = segments.Should().ContainSingle().Subject;
        segment.StartMs.Should().Be(0);
        segment.EndMs.Should().Be(1_100);
        segment.Pcm16.Length.Should().Be(11 * FrameBytes);
    }

    [Fact]
    public void Should_StartAtFirstSpeechFrame_When_SilenceComesFirst()
    {
        var segments = Feed(NewBuffer(), (Silent(), 4), (Loud(), 3), (Silent(), 6));

        var segment = segments.Should().ContainSingle().Subject;
        segment.StartMs.Should().Be(400);
        segment.EndMs.Should().Be(700);
    }

    [Fact]
    public void Should_CutSegment_When_SpeechReaches15Seconds()
    {
        var buffer = NewBuffer();
        Feed(buffer, (Loud(), 149)).Should().BeEmpty();

        var first = buffer.Push(Loud());

        first.Should().NotBeNull();
        first.StartMs.Should().Be(0);
        first.EndMs.Should().Be(15_000);
        first.Pcm16.Length.Should().Be(150 * FrameBytes);
    }

    [Fact]
    public void Should_StartNextSegmentAfterCut_When_SpeechContinues()
    {
        var segments = Feed(NewBuffer(), (Loud(), 160), (Silent(), 6));

        segments.Should().HaveCount(2);
        segments[0].EndMs.Should().Be(15_000);
        segments[1].StartMs.Should().Be(15_000);
        segments[1].EndMs.Should().Be(16_000);
    }

    [Fact]
    public void Should_IgnoreSpeech_When_ShorterThanMinimum()
    {
        var segments = Feed(NewBuffer(), (Loud(), 2), (Silent(), 6));

        segments.Should().BeEmpty();
    }

    [Fact]
    public void Should_KeepSpeech_When_ExactlyMinimumLength()
    {
        var segments = Feed(NewBuffer(), (Loud(), 3), (Silent(), 6));

        var segment = segments.Should().ContainSingle().Subject;
        segment.EndMs.Should().Be(300);
    }

    [Fact]
    public void Should_KeepTimestampsMoving_When_SpeechIsDiscarded()
    {
        var segments = Feed(NewBuffer(), (Loud(), 1), (Silent(), 6), (Loud(), 4), (Silent(), 6));

        var segment = segments.Should().ContainSingle().Subject;
        segment.StartMs.Should().Be(700);
        segment.EndMs.Should().Be(1_100);
    }

    [Fact]
    public void Should_EmitSegmentsInOrder_When_SeveralUtterances()
    {
        var segments = Feed(NewBuffer(), (Loud(), 4), (Silent(), 10), (Loud(), 5), (Silent(), 10), (Loud(), 6), (Silent(), 6));

        segments.Should().HaveCount(3);
        segments.Select(s => s.StartMs).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        segments.Zip(segments.Skip(1)).Should().OnlyContain(pair => pair.First.EndMs <= pair.Second.StartMs);
    }

    [Fact]
    public void Should_KeepSpeakersIndependent_When_SeparateBuffersInterleave()
    {
        var interviewer = NewBuffer();
        var candidate = NewBuffer();
        var fromInterviewer = new List<SpeechSegment>();
        var fromCandidate = new List<SpeechSegment>();

        // Interviewer talks frames 0-4, candidate frames 3-9 (overlap); both then go quiet.
        for (var frame = 0; frame < 30; frame++)
        {
            var interviewerFrame = frame < 5 ? Loud() : Silent();
            var candidateFrame = frame is >= 3 and < 10 ? Loud() : Silent();
            var a = interviewer.Push(interviewerFrame);
            var b = candidate.Push(candidateFrame);
            if (a is not null)
            {
                fromInterviewer.Add(a);
            }

            if (b is not null)
            {
                fromCandidate.Add(b);
            }
        }

        fromInterviewer.Should().ContainSingle().Which.Should().Match<SpeechSegment>(s => s.StartMs == 0 && s.EndMs == 500);
        fromCandidate.Should().ContainSingle().Which.Should().Match<SpeechSegment>(s => s.StartMs == 300 && s.EndMs == 1_000);
    }

    [Fact]
    public void Should_UseConfiguredValues_When_OptionsChange()
    {
        var buffer = new VoiceActivityBuffer(new VadOptions { PauseMs = 200, MinSpeechMs = 100, MaxSegmentMs = 1_000 });

        var segments = Feed(buffer, (Loud(), 1), (Silent(), 2));

        segments.Should().ContainSingle().Which.EndMs.Should().Be(100);
    }

    [Fact]
    public void Should_ThrowArgumentNull_When_OptionsMissing()
    {
        var act = () => new VoiceActivityBuffer(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Should_TreatEmptyFrameAsSilence()
    {
        var buffer = NewBuffer();

        buffer.Push(ReadOnlyMemory<byte>.Empty).Should().BeNull();
    }

    [Fact]
    public void Should_FlushSpeechInProgress_When_StreamEndsMidSentence()
    {
        var buffer = NewBuffer();
        Feed(buffer, (Silent(), 2), (Loud(), 4)).Should().BeEmpty();

        var segment = buffer.Flush();

        segment.Should().NotBeNull();
        segment.StartMs.Should().Be(200);
        segment.EndMs.Should().Be(600);
        segment.Pcm16.Length.Should().Be(4 * FrameBytes);
    }

    [Fact]
    public void Should_TrimTrailingSilence_When_FlushingAfterShortPause()
    {
        var buffer = NewBuffer();
        Feed(buffer, (Loud(), 4), (Silent(), 3)).Should().BeEmpty();

        var segment = buffer.Flush();

        segment.Should().NotBeNull();
        segment.EndMs.Should().Be(400);
        segment.Pcm16.Length.Should().Be(4 * FrameBytes);
    }

    [Fact]
    public void Should_FlushNothing_When_OnlySilenceBuffered()
    {
        var buffer = NewBuffer();
        Feed(buffer, (Silent(), 20));

        buffer.Flush().Should().BeNull();
    }

    [Fact]
    public void Should_FlushNothing_When_NothingWasPushed()
    {
        NewBuffer().Flush().Should().BeNull();
    }

    [Fact]
    public void Should_FlushNothing_When_SpeechShorterThanMinimum()
    {
        var buffer = NewBuffer();
        Feed(buffer, (Loud(), 2));

        buffer.Flush().Should().BeNull();
    }

    [Fact]
    public void Should_FlushSpeech_When_ExactlyMinimumLength()
    {
        var buffer = NewBuffer();
        Feed(buffer, (Loud(), 3));

        buffer.Flush().Should().NotBeNull().And.Match<SpeechSegment>(s => s.EndMs == 300);
    }

    [Fact]
    public void Should_ResetState_When_Flushed()
    {
        var buffer = NewBuffer();
        Feed(buffer, (Loud(), 4));
        buffer.Flush().Should().NotBeNull();

        buffer.Flush().Should().BeNull("a second flush has nothing left");

        var next = Feed(buffer, (Loud(), 3), (Silent(), 6));
        var segment = next.Should().ContainSingle().Subject;
        segment.StartMs.Should().Be(400, "timestamps keep moving after a flush");
        segment.Pcm16.Length.Should().Be(3 * FrameBytes);
    }

    [Fact]
    public void Should_StayBounded_When_SpeechNeverPauses()
    {
        var buffer = NewBuffer();
        var maxBytes = 0;

        for (var i = 0; i < 1_000; i++)
        {
            var segment = buffer.Push(Loud());
            if (segment is not null)
            {
                maxBytes = Math.Max(maxBytes, segment.Pcm16.Length);
            }
        }

        maxBytes.Should().Be(150 * FrameBytes);
    }
}
