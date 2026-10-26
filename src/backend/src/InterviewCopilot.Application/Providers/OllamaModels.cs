namespace InterviewCopilot.Application.Providers;

/// <summary>A model that is installed in the local Ollama (name as Ollama reports it, e.g. "llama3.2:3b").</summary>
public sealed record OllamaModelInfo(string Name, long SizeBytes, DateTimeOffset ModifiedAt);

/// <summary>Lists the models the user has already pulled into Ollama (#31).</summary>
public interface IOllamaModelCatalog
{
    /// <summary>Installed models, or throws when Ollama is unreachable.</summary>
    /// <exception cref="LlmUnavailableException">Ollama can't be reached.</exception>
    Task<IReadOnlyList<OllamaModelInfo>> ListInstalledAsync(CancellationToken cancellationToken);
}
