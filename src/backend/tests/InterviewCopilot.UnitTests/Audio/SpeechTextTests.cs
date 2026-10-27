using FluentAssertions;
using InterviewCopilot.Application.Audio;

namespace InterviewCopilot.UnitTests.Audio;

public class SpeechTextTests
{
    [Theory]
    [InlineData("(mumbles)")]
    [InlineData(" [BLANK_AUDIO] ")]
    [InlineData("(music) [inaudible]")]
    [InlineData("*coughs*")]
    [InlineData("")]
    [InlineData("   ")]
    public void Should_TreatWhisperNoiseLabelsAsNoSpeech(string text)
    {
        SpeechText.IsNoSpeech(text).Should().BeTrue();
    }

    [Theory]
    [InlineData("I built microservices.")]
    [InlineData("I built (mostly) microservices.")]
    [InlineData("(mumbles) then I scaled it")]
    public void Should_KeepRealSpeech_EvenWithALabelInside(string text)
    {
        SpeechText.IsNoSpeech(text).Should().BeFalse();
    }
}
