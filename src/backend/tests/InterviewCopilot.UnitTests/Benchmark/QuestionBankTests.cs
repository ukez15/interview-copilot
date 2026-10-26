using FluentAssertions;
using InterviewCopilot.Benchmark;

namespace InterviewCopilot.UnitTests.Benchmark;

public sealed class QuestionBankTests
{
    private static readonly string[] AllTypes =
    [
        "Behavioral", "HowDoYou", "Situational", "TechnicalKnowledge",
        "SystemDesign", "Coding", "TellMeAboutYourself", "Experience",
    ];

    [Fact]
    public async Task ShippedQuestionsFile_ShouldHaveTwentyQuestionsCoveringAllEightTypes()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "questions.json");

        var questions = await QuestionBank.LoadAsync(path, CancellationToken.None);

        questions.Should().HaveCount(20);
        questions.Select(q => q.Text).Should().OnlyHaveUniqueItems().And.OnlyContain(t => !string.IsNullOrWhiteSpace(t));
        foreach (var type in AllTypes)
        {
            questions.Count(q => q.Type == type).Should().BeGreaterThanOrEqualTo(2, $"type {type} needs at least two questions");
        }
    }

    [Fact]
    public async Task LoadAsync_ShouldRejectUnknownType()
    {
        var path = WriteTemp("""[{"type":"Nonsense","text":"Hi?"}]""");

        var act = () => QuestionBank.LoadAsync(path, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*Nonsense*");
    }

    [Fact]
    public async Task LoadAsync_ShouldRejectEmptyFileAndBlankText()
    {
        var empty = () => QuestionBank.LoadAsync(WriteTemp("[]"), CancellationToken.None);
        var blank = () => QuestionBank.LoadAsync(WriteTemp("""[{"type":"Coding","text":"  "}]"""), CancellationToken.None);

        await empty.Should().ThrowAsync<InvalidDataException>();
        await blank.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task LoadAsync_ShouldRejectMissingFile()
    {
        var act = () => QuestionBank.LoadAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"), CancellationToken.None);

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task LoadAsync_ShouldRejectMalformedJson()
    {
        var act = () => QuestionBank.LoadAsync(WriteTemp("{not json"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    private static string WriteTemp(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ic-bench-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }
}
