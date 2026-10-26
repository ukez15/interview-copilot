using System.Text.Json;

namespace InterviewCopilot.Benchmark;

internal static class QuestionBank
{
    /// <summary>The backend's eight question types, as serialized by its string enum converter.</summary>
    public static readonly IReadOnlyList<string> KnownTypes =
    [
        "Behavioral", "HowDoYou", "Situational", "TechnicalKnowledge",
        "SystemDesign", "Coding", "TellMeAboutYourself", "Experience",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<IReadOnlyList<Question>> LoadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Questions file not found: {path}", path);
        }

        List<Question>? questions;
        try
        {
            await using var stream = File.OpenRead(path);
            questions = await JsonSerializer.DeserializeAsync<List<Question>>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Questions file is not valid JSON: {ex.Message}", ex);
        }

        if (questions is null || questions.Count == 0)
        {
            throw new InvalidDataException("Questions file has no questions.");
        }

        foreach (var question in questions)
        {
            if (string.IsNullOrWhiteSpace(question.Text))
            {
                throw new InvalidDataException("Every question needs non-empty text.");
            }

            if (!KnownTypes.Contains(question.Type))
            {
                throw new InvalidDataException($"Unknown question type '{question.Type}'. Expected one of: {string.Join(", ", KnownTypes)}.");
            }
        }

        return questions;
    }
}
