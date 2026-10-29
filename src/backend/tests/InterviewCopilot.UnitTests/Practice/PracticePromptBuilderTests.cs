using FluentAssertions;
using InterviewCopilot.Application.Copilot;
using InterviewCopilot.Application.Practice;
using InterviewCopilot.Application.Providers;
using InterviewCopilot.Domain.Entities;
using InterviewCopilot.Domain.Enums;

namespace InterviewCopilot.UnitTests.Practice;

public class PracticePromptBuilderTests
{
    private static Session NewSession()
    {
        var session = Session.Create(Guid.NewGuid(), "Mock", SessionMode.MockInterview, FixedClock.Default);
        session.ConfigureAnswers(AnswerLength.Balanced, AnswerFramework.Star, null, "Senior backend role");
        session.AttachDocument("jd.txt", "Requirements: C#, distributed systems, mentoring skills.", FixedClock.Default);
        return session;
    }

    [Fact]
    public void Should_AskNextQuestion_WithoutRepeating()
    {
        var messages = PracticePromptBuilder.NextQuestion(NewSession(), ["Tell me about yourself."], ProviderNames.Ollama);

        messages[0].Content.Should().Contain("interviewer").And.Contain("Senior backend role");
        messages[1].Content.Should().Contain("<document name=\"jd.txt\">").And.Contain("- Tell me about yourself.");
    }

    private static Session SessionWithLongDocument()
    {
        var session = Session.Create(Guid.NewGuid(), "Mock", SessionMode.MockInterview, FixedClock.Default);
        session.AttachDocument("cv.txt", new string('z', 10_000), FixedClock.Default);
        return session;
    }

    [Fact]
    public void Should_KeepQuestionDocumentsWithinTheQuestionBudget_And_ScoringWithinTheDefault()
    {
        var session = SessionWithLongDocument();

        var asked = PracticePromptBuilder.NextQuestion(session, [], ProviderNames.Ollama);
        var scored = PracticePromptBuilder.ScoreAnswer(session, "Why?", "Yes.");

        asked[1].Content.Count(c => c == 'z').Should().BeLessThanOrEqualTo(PracticePromptBuilder.QuestionDocumentBudget);
        scored[1].Content.Count(c => c == 'z').Should().BeGreaterThan(PracticePromptBuilder.QuestionDocumentBudget);
    }

    [Theory]
    [InlineData(ProviderNames.Anthropic)]
    [InlineData(ProviderNames.OpenAi)]
    [InlineData(ProviderNames.Google)]
    public void Should_GiveCloudModelsTheFullDocumentBudget_When_Asking(string provider)
    {
        var asked = PracticePromptBuilder.NextQuestion(SessionWithLongDocument(), [], provider);

        PracticePromptBuilder.QuestionDocumentBudgetFor(provider).Should().Be(DocumentContextSelector.DefaultCharacterBudget);
        asked[1].Content.Count(c => c == 'z').Should().BeGreaterThan(PracticePromptBuilder.QuestionDocumentBudget);
    }

    [Theory]
    [InlineData("ollama")]
    [InlineData("Ollama")]
    public void Should_LimitOnlyTheLocalModel(string provider)
    {
        PracticePromptBuilder.QuestionDocumentBudgetFor(provider).Should().Be(PracticePromptBuilder.QuestionDocumentBudget);
    }

    [Fact]
    public void Should_AskForScoreFormat()
    {
        var messages = PracticePromptBuilder.ScoreAnswer(NewSession(), "Why C#?", "Because it's great.");

        messages[1].Content.Should().Contain("SCORE:").And.Contain("FEEDBACK:").And.Contain("Because it's great.");
    }

    [Fact]
    public void Should_ParseWellFormedFeedback()
    {
        var feedback = PracticePromptBuilder.ParseFeedback("SCORE: 7\nFEEDBACK: Clear structure.\nAdd metrics.");

        feedback!.Score.Should().Be(7);
        feedback.Feedback.Should().StartWith("Clear structure.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("SCORE: 7")]
    [InlineData("SCORE: 15\nFEEDBACK: too high")]
    [InlineData("FEEDBACK: no score")]
    public void Should_RejectMalformedFeedback(string reply)
    {
        PracticePromptBuilder.ParseFeedback(reply).Should().BeNull();
    }
}
