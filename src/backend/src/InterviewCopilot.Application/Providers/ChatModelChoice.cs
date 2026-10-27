using InterviewCopilot.Domain.Entities;

namespace InterviewCopilot.Application.Providers;

/// <summary>
/// The chat provider and model a user's requests go to: their choice in Settings, else the configured default
/// (Ollama). Live answers and practice both use it, so they never disagree (#30, #82).
/// </summary>
/// <param name="Provider">Registered provider name, such as "ollama" or "anthropic".</param>
/// <param name="Model">The model to ask.</param>
public sealed record ChatModelChoice(string Provider, string Model)
{
    /// <summary>Picks the provider and model for the owner of a session.</summary>
    /// <param name="owner">The owner's saved preferences, or null when they never chose.</param>
    /// <param name="options">The configured defaults.</param>
    /// <returns>The provider and model to use.</returns>
    public static ChatModelChoice For(UserPreferences? owner, LlmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var provider = string.IsNullOrWhiteSpace(owner?.LlmProvider) ? options.Provider : owner.LlmProvider;
        var model = string.IsNullOrWhiteSpace(owner?.LlmModel) ? options.Model : owner.LlmModel;
        return new ChatModelChoice(provider, model);
    }
}
