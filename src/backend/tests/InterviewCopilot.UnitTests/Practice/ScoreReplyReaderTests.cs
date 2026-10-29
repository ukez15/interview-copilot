using FluentAssertions;
using InterviewCopilot.Application.Practice;

namespace InterviewCopilot.UnitTests.Practice;

public class ScoreReplyReaderTests
{
    [Fact]
    public void Should_GiveTheScore_OnlyOnceItsLineIsComplete()
    {
        var reader = new ScoreReplyReader();

        reader.Add("SCORE: 1").Score.Should().BeNull("it may still become 10");
        var read = reader.Add("0\n");

        read.Score.Should().Be(10);
        reader.Add("FEEDBACK: Good.").Score.Should().BeNull("the score is only given once");
    }

    [Fact]
    public void Should_StreamTheFeedback_PieceByPiece_WithoutTheLabel()
    {
        var reader = new ScoreReplyReader();
        var pieces = new List<string>();

        foreach (var token in new[] { "SCORE: 7\n", "feedback:", "  Clear ", "structure", ".\nAdd numbers." })
        {
            pieces.Add(reader.Add(token).Feedback);
        }

        string.Concat(pieces).Should().Be("Clear structure.\nAdd numbers.");
        reader.Reply.Should().Be("SCORE: 7\nfeedback:  Clear structure.\nAdd numbers.");
    }

    [Theory]
    [InlineData("SCORE: 15\nFEEDBACK: too high")]
    [InlineData("I think it was good.")]
    public void Should_GiveNoScore_When_TheReplyDoesNotFollowTheFormat(string reply)
    {
        var reader = new ScoreReplyReader();

        reader.Add(reply).Score.Should().BeNull();
    }

    [Fact]
    public void Should_GiveNoFeedback_BeforeTheFeedbackLabel()
    {
        var reader = new ScoreReplyReader();

        reader.Add("SCORE: 7\nThe answer was").Feedback.Should().BeEmpty();
    }
}
