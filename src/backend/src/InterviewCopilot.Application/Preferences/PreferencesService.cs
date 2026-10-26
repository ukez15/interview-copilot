using InterviewCopilot.Application.Common.Interfaces;
using InterviewCopilot.Application.Providers;
using InterviewCopilot.Domain.Entities;
using InterviewCopilot.Domain.Exceptions;
using Microsoft.Extensions.Options;

namespace InterviewCopilot.Application.Preferences;

/// <summary>
/// The signed-in account's choices. Null values mean "use the configured default", which the
/// <c>Default*</c> fields name so the UI can show it.
/// </summary>
/// <param name="LlmModel">The chosen model, or null for the default.</param>
/// <param name="DefaultLlmModel">The model from configuration (<c>Llm:Model</c>).</param>
/// <param name="LlmProvider">The chosen chat provider (#30), or null for the default.</param>
/// <param name="DefaultLlmProvider">The provider from configuration (<c>Llm:Provider</c>).</param>
/// <param name="SpeechToTextProvider">The chosen speech-to-text provider (#20), or null for the default.</param>
/// <param name="DefaultSpeechToTextProvider">The provider from configuration (<c>Transcription:Provider</c>).</param>
public sealed record PreferencesDto(
    string? LlmModel,
    string DefaultLlmModel,
    string? LlmProvider,
    string DefaultLlmProvider,
    string? SpeechToTextProvider,
    string DefaultSpeechToTextProvider);

/// <summary>Reads and writes per-account preferences (#31, #30, #20).</summary>
public sealed class PreferencesService(
    IUserPreferencesRepository preferences,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    IOptions<LlmOptions> llmOptions,
    IOptions<TranscriptionOptions> transcriptionOptions,
    ProviderCatalog catalog)
{
    public async Task<PreferencesDto> GetAsync(Guid userId, CancellationToken cancellationToken) =>
        ToDto(await preferences.GetAsync(userId, cancellationToken));

    /// <summary>Saves the chosen model for the current provider; blank or null clears it.</summary>
    public Task<PreferencesDto> SetLlmModelAsync(Guid userId, string? model, CancellationToken cancellationToken) =>
        UpdateAsync(userId, (p, now) => p.SetLlmModel(model, now), cancellationToken);

    /// <summary>Chooses the chat provider and model together. The provider must be one the catalog offers.</summary>
    public Task<PreferencesDto> SetLlmAsync(Guid userId, string? provider, string? model, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(provider)
            && !catalog.LlmProviders().Any(p => string.Equals(p.Name, provider.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            throw new DomainException("That model provider isn't available.");
        }

        return UpdateAsync(userId, (p, now) => p.SetLlm(provider, model, now), cancellationToken);
    }

    /// <summary>Chooses the speech-to-text provider. Disabled providers (Deepgram by default) are refused.</summary>
    public Task<PreferencesDto> SetSpeechToTextProviderAsync(Guid userId, string? provider, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(provider) && !catalog.IsSpeechToTextProviderAvailable(provider.Trim()))
        {
            throw new DomainException("That speech-to-text provider isn't available.");
        }

        return UpdateAsync(userId, (p, now) => p.SetSpeechToTextProvider(provider, now), cancellationToken);
    }

    private async Task<PreferencesDto> UpdateAsync(
        Guid userId,
        Action<UserPreferences, DateTimeOffset> change,
        CancellationToken cancellationToken)
    {
        var existing = await preferences.GetAsync(userId, cancellationToken);
        var now = clock.GetUtcNow();
        var target = existing ?? UserPreferences.Create(userId, now);

        change(target, now);
        if (existing is null)
        {
            preferences.Add(target);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ToDto(target);
    }

    private PreferencesDto ToDto(UserPreferences? p) => new(
        p?.LlmModel,
        llmOptions.Value.Model,
        p?.LlmProvider,
        llmOptions.Value.Provider,
        p?.SpeechToTextProvider,
        transcriptionOptions.Value.Provider);
}
