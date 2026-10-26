namespace InterviewCopilot.Application.Providers;

/// <summary>The model provider can't be reached or rejected the request. Message is user-facing.</summary>
public sealed class LlmUnavailableException : Exception
{
    public LlmUnavailableException()
    {
    }

    public LlmUnavailableException(string message)
        : base(message)
    {
    }

    public LlmUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Speech-to-text failed (model missing, provider disabled, bad key). Message is user-facing.</summary>
public sealed class TranscriptionUnavailableException : Exception
{
    public TranscriptionUnavailableException()
    {
    }

    public TranscriptionUnavailableException(string message)
        : base(message)
    {
    }

    public TranscriptionUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The cloud provider rejected the BYOK key (HTTP 401/403). Message is user-facing and never contains the key.</summary>
public sealed class LlmKeyRejectedException : Exception
{
    public LlmKeyRejectedException()
    {
    }

    public LlmKeyRejectedException(string message)
        : base(message)
    {
    }

    public LlmKeyRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
