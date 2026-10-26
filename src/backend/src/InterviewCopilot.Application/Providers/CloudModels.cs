namespace InterviewCopilot.Application.Providers;

/// <summary>A model a cloud provider offers to this key (#30).</summary>
public sealed record CloudModelInfo(string Id, string DisplayName);

/// <summary>Lists the models a cloud chat provider offers for a BYOK key, straight from the provider's API.</summary>
public interface ICloudModelCatalog
{
    /// <summary>The provider this catalog serves (one of <see cref="ProviderNames"/>).</summary>
    string Provider { get; }

    /// <summary>Models available to <paramref name="apiKey"/>. The key is never stored or logged.</summary>
    /// <exception cref="LlmKeyRejectedException">The provider rejected the key.</exception>
    /// <exception cref="LlmUnavailableException">The provider can't be reached or is rate limiting.</exception>
    Task<IReadOnlyList<CloudModelInfo>> ListAsync(string apiKey, CancellationToken cancellationToken);
}
