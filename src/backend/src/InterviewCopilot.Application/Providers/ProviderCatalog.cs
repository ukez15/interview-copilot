using Microsoft.Extensions.Options;

namespace InterviewCopilot.Application.Providers;

public sealed record ProviderInfo(string Name, string DisplayName, bool IsLocal, bool RequiresApiKey, bool IsDefault);

/// <summary>What the Settings screen may offer (FR-15). Disabled providers are never listed.</summary>
public sealed class ProviderCatalog(IOptions<TranscriptionOptions> transcriptionOptions, IOptions<LlmOptions> llmOptions)
{
    public IReadOnlyList<ProviderInfo> SpeechToTextProviders()
    {
        var options = transcriptionOptions.Value;
        var providers = new List<ProviderInfo>
        {
            new(ProviderNames.Whisper, "Whisper (local, free)", IsLocal: true, RequiresApiKey: false,
                IsDefault: options.Provider == ProviderNames.Whisper),
        };

        if (options.Providers.Deepgram.Enabled)
        {
            providers.Add(new ProviderInfo(ProviderNames.Deepgram, "Deepgram (cloud, paid)", IsLocal: false,
                RequiresApiKey: true, IsDefault: options.Provider == ProviderNames.Deepgram));
        }

        return providers;
    }

    public IReadOnlyList<ProviderInfo> LlmProviders()
    {
        var options = llmOptions.Value;
        return
        [
            new(ProviderNames.Ollama, "Ollama (local, free)", IsLocal: true, RequiresApiKey: false,
                IsDefault: IsConfigured(options, ProviderNames.Ollama)),
            Cloud(options, ProviderNames.Anthropic, "Anthropic (cloud, your key)"),
            Cloud(options, ProviderNames.OpenAi, "OpenAI (cloud, your key)"),
            Cloud(options, ProviderNames.Google, "Google Gemini (cloud, your key)"),
        ];
    }

    private static ProviderInfo Cloud(LlmOptions options, string name, string displayName) =>
        new(name, displayName, IsLocal: false, RequiresApiKey: true, IsDefault: IsConfigured(options, name));

    private static bool IsConfigured(LlmOptions options, string name) =>
        string.Equals(options.Provider, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Guards against using a provider that is disabled in configuration.</summary>
    public bool IsSpeechToTextProviderAvailable(string name) =>
        SpeechToTextProviders().Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}
