using InterviewCopilot.Domain.Exceptions;

namespace InterviewCopilot.Domain.Entities;

/// <summary>Per-account choices stored on the backend so automatic answers can use them (#31).</summary>
public sealed class UserPreferences
{
    public const int MaxModelLength = 200;
    public const int MaxProviderLength = 50;

    private UserPreferences()
    {
    }

    /// <summary>The account these preferences belong to (also the key).</summary>
    public Guid UserId { get; private set; }

    /// <summary>Ollama model name, e.g. "llama3.2:3b". Null means the configured default.</summary>
    public string? LlmModel { get; private set; }

    /// <summary>Chat model provider, e.g. "ollama" or "openai" (#30). Null means the configured default.</summary>
    public string? LlmProvider { get; private set; }

    /// <summary>Speech-to-text provider, e.g. "whisper" or "deepgram" (#20). Null means the configured default.</summary>
    public string? SpeechToTextProvider { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static UserPreferences Create(Guid userId, DateTimeOffset now) =>
        new() { UserId = userId, UpdatedAt = now };

    public void SetLlmModel(string? model, DateTimeOffset now)
    {
        var trimmed = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        if (trimmed is { Length: > MaxModelLength })
        {
            throw new DomainException($"Model names can be at most {MaxModelLength} characters.");
        }

        LlmModel = trimmed;
        UpdatedAt = now;
    }

    /// <summary>Chooses the chat provider and its model together; blank values mean the configured defaults.</summary>
    public void SetLlm(string? provider, string? model, DateTimeOffset now)
    {
        var normalized = NormalizeProvider(provider);
        SetLlmModel(model, now);
        LlmProvider = normalized;
    }

    public void SetSpeechToTextProvider(string? provider, DateTimeOffset now)
    {
        SpeechToTextProvider = NormalizeProvider(provider);
        UpdatedAt = now;
    }

    private static string? NormalizeProvider(string? provider)
    {
        var trimmed = string.IsNullOrWhiteSpace(provider) ? null : provider.Trim().ToLowerInvariant();
        if (trimmed is { Length: > MaxProviderLength })
        {
            throw new DomainException($"Provider names can be at most {MaxProviderLength} characters.");
        }

        return trimmed;
    }
}
