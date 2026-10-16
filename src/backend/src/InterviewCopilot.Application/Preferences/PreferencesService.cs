using InterviewCopilot.Application.Common.Interfaces;
using InterviewCopilot.Application.Providers;
using InterviewCopilot.Domain.Entities;
using Microsoft.Extensions.Options;

namespace InterviewCopilot.Application.Preferences;

/// <summary>
/// The signed-in account's choices. A null <paramref name="LlmModel"/> means "use the configured default",
/// which <paramref name="DefaultLlmModel"/> names so the UI can show it.
/// </summary>
/// <param name="LlmModel">The chosen model, or null for the default.</param>
/// <param name="DefaultLlmModel">The model from configuration (<c>Llm:Model</c>).</param>
public sealed record PreferencesDto(string? LlmModel, string DefaultLlmModel);

/// <summary>Reads and writes per-account preferences (#31).</summary>
public sealed class PreferencesService(
    IUserPreferencesRepository preferences,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    IOptions<LlmOptions> llmOptions)
{
    public async Task<PreferencesDto> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        var existing = await preferences.GetAsync(userId, cancellationToken);
        return ToDto(existing?.LlmModel);
    }

    /// <summary>Saves the chosen Ollama model; blank or null clears it. Creates the record on first write.</summary>
    public async Task<PreferencesDto> SetLlmModelAsync(Guid userId, string? model, CancellationToken cancellationToken)
    {
        var existing = await preferences.GetAsync(userId, cancellationToken);
        var now = clock.GetUtcNow();
        var target = existing ?? UserPreferences.Create(userId, now);

        target.SetLlmModel(model, now);
        if (existing is null)
        {
            preferences.Add(target);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ToDto(target.LlmModel);
    }

    private PreferencesDto ToDto(string? model) => new(model, llmOptions.Value.Model);
}
