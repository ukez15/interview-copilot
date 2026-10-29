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
/// <param name="ApproximateBytes">About how big the download is, to show before it starts (#85); null when unknown.</param>
public sealed record WhisperModelStatus(
    WhisperModelState State,
    string Model,
    long BytesDownloaded,
    long? TotalBytes,
    string? Message,
    long? ApproximateBytes = null);

/// <summary>Outcome of asking for a download.</summary>
public enum WhisperDownloadStart
{
    Started,
    AlreadyDownloading,
    AlreadyReady,
}

/// <summary>
/// The local speech models a user can choose from (#85), their states, and their (user-initiated) downloads.
/// Models are named by file name; only the ones offered here can be downloaded or chosen.
/// </summary>
public interface IWhisperModelStatus
{
    /// <summary>The offered models, the configured default first.</summary>
    IReadOnlyList<WhisperModelStatus> GetModels();

    /// <summary>The current state of one offered model; null when it isn't offered.</summary>
    WhisperModelStatus? GetStatus(string model);

    /// <summary>Starts the model's download unless one is running or it's already present; null when it isn't offered.</summary>
    WhisperDownloadStart? StartDownload(string model);
}
