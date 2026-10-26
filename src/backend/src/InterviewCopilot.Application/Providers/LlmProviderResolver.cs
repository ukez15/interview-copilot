namespace InterviewCopilot.Application.Providers;

/// <summary>Picks the registered <see cref="ILlmProvider"/> by name (Ollama by default; BYOK providers from #30).</summary>
public interface ILlmProviderResolver
{
    /// <summary>Returns the provider whose name matches <paramref name="name"/>, ignoring case.</summary>
    /// <exception cref="LlmUnavailableException">No provider with that name is registered.</exception>
    ILlmProvider Resolve(string name);
}

public sealed class LlmProviderResolver(IEnumerable<ILlmProvider> providers) : ILlmProviderResolver
{
    public ILlmProvider Resolve(string name) =>
        providers.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new LlmUnavailableException($"The model provider '{name}' isn't available.");
}
