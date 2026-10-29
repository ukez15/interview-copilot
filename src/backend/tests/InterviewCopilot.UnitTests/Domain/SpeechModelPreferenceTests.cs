using FluentAssertions;
using InterviewCopilot.Domain.Entities;
using InterviewCopilot.Domain.Exceptions;

namespace InterviewCopilot.UnitTests.Domain;

public class SpeechModelPreferenceTests
{
    private static readonly DateTimeOffset Now = FixedClock.Default;

    [Fact]
    public void Should_SaveTheChosenSpeechModel_AndTreatBlankAsTheDefault()
    {
        var preferences = UserPreferences.Create(Guid.NewGuid(), Now);

        preferences.SetSpeechModel(" ggml-large-v3-turbo-q5_0.bin ", Now.AddMinutes(1));
        preferences.SpeechModel.Should().Be("ggml-large-v3-turbo-q5_0.bin");
        preferences.UpdatedAt.Should().Be(Now.AddMinutes(1));

        preferences.SetSpeechModel(" ", Now);
        preferences.SpeechModel.Should().BeNull();
    }

    [Fact]
    public void Should_RejectAModelNameThatIsTooLong()
    {
        var preferences = UserPreferences.Create(Guid.NewGuid(), Now);

        var act = () => preferences.SetSpeechModel(new string('x', UserPreferences.MaxModelLength + 1), Now);

        act.Should().Throw<DomainException>();
    }
}
