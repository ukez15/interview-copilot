using System.Text.RegularExpressions;

namespace InterviewCopilot.Application.Audio;

/// <summary>
/// Reads Whisper's text. Whisper labels noise and unclear sound instead of words, such as "(mumbles)",
/// "[BLANK_AUDIO]" or "*coughs*"; a segment that is only such labels holds no speech (#78).
/// </summary>
public static partial class SpeechText
{
    /// <summary>True when the text has no words: empty, or nothing but bracketed labels.</summary>
    /// <param name="text">A transcribed segment.</param>
    /// <returns>True when the segment should be treated as silence.</returns>
    public static bool IsNoSpeech(string? text) => string.IsNullOrWhiteSpace(text) || OnlyLabels().IsMatch(text);

    [GeneratedRegex(@"^\s*(?:(?:\([^)]*\)|\[[^\]]*\]|\*[^*]*\*)\s*)+$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex OnlyLabels();
}
