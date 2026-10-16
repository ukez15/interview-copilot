namespace InterviewCopilot.Application.Providers;

/// <summary>Where the local speech model is in its life cycle.</summary>
public enum WhisperModelState
{
    Missing,
    Downloading,
    Ready,
    Failed,
}

/// <summary>Snapshot of the speech model. TotalBytes is null when the size isn't known.</summary>
/// <param name="State">Current life-cycle state.</param>
/// <param name="Model">Configured model file name.</param>
/// <param name="BytesDownloaded">Bytes received so far while downloading.</param>
/// <param name="TotalBytes">Expected size, or null when unknown.</param>
/// <param name="Message">User-facing failure message when the state is Failed.</param>
public sealed record WhisperModelStatus(
    WhisperModelState State,
    string Model,
    long BytesDownloaded,
    long? TotalBytes,
    string? Message);

/// <summary>Outcome of asking for a download.</summary>
public enum WhisperDownloadStart
{
    Started,
    AlreadyDownloading,
    AlreadyReady,
}

/// <summary>Reports the speech model state and starts its (user-initiated) download.</summary>
public interface IWhisperModelStatus
{
    /// <summary>Current state of the configured model.</summary>
    WhisperModelStatus GetStatus();

    /// <summary>Starts the download unless one is running or the model is already present.</summary>
    WhisperDownloadStart StartDownload();
}
