using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using InterviewCopilot.Domain.Entities;

namespace InterviewCopilot.Application.Practice;

/// <summary>What one more piece of the score reply revealed.</summary>
/// <param name="Score">The score, given once, as soon as its line is complete; otherwise null.</param>
/// <param name="Feedback">New feedback text, without the "FEEDBACK:" label; empty when there's none yet.</param>
public readonly record struct ScoreReplyRead(int? Score, string Feedback);

/// <summary>
/// Reads the model's "SCORE: n / FEEDBACK: …" reply as it streams in (#78), so the overlay shows the score as
/// soon as it's written and the feedback word by word. <see cref="PracticePromptBuilder.ParseFeedback"/> still
/// decides, on the whole reply, whether it can be saved.
/// </summary>
public sealed partial class ScoreReplyReader
{
    private const string FeedbackLabel = "FEEDBACK:";

    private readonly StringBuilder _reply = new();
    private bool _scoreGiven;
    private int _feedbackGiven;

    /// <summary>The reply so far.</summary>
    public string Reply => _reply.ToString();

    /// <summary>Adds the next piece of the reply.</summary>
    /// <param name="token">Text the model just wrote.</param>
    /// <returns>A score seen for the first time, and any new feedback text.</returns>
    public ScoreReplyRead Add(string token)
    {
        _reply.Append(token);
        var text = _reply.ToString();
        var label = text.IndexOf(FeedbackLabel, StringComparison.OrdinalIgnoreCase);
        return new ScoreReplyRead(NewScore(label < 0 ? text : text[..label]), NewFeedback(text, label));
    }

    private int? NewScore(string beforeFeedback)
    {
        var match = _scoreGiven ? Match.Empty : ScoreLine().Match(beforeFeedback);
        // Only a finished line counts: "SCORE: 1" may still become 10.
        if (!match.Success || match.Index + match.Length >= beforeFeedback.Length)
        {
            return null;
        }

        var score = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        if (score is < PracticeAttempt.MinScore or > PracticeAttempt.MaxScore)
        {
            return null;
        }

        _scoreGiven = true;
        return score;
    }

    private string NewFeedback(string text, int label)
    {
        if (label < 0)
        {
            return string.Empty;
        }

        var feedback = text[(label + FeedbackLabel.Length)..].TrimStart();
        var fresh = feedback[_feedbackGiven..];
        _feedbackGiven = feedback.Length;
        return fresh;
    }

    [GeneratedRegex(@"SCORE:\s*(\d{1,2})(?!\d)", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ScoreLine();
}
