namespace InterviewCopilot.Api.Infrastructure;

/// <summary>Names of the authorization policies.</summary>
internal static class Policies
{
    /// <summary>
    /// Endpoints a paired browser extension may call (#33): list live sessions, ask for answers, and the
    /// session events socket. Every other endpoint rejects pairing tokens.
    /// </summary>
    public const string ExtensionScope = "extension-scope";
}
