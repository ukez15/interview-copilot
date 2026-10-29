using InterviewCopilot.Application.Providers;

namespace InterviewCopilot.UnitTests;

/// <summary>Offered speech models whose states a test sets by hand.</summary>
internal sealed class FakeSpeechModels : IWhisperModelStatus
{
    public const string Standard = "ggml-small.en.bin";
    public const string Accurate = "ggml-large-v3-turbo-q5_0.bin";

    public Dictionary<string, WhisperModelState> States { get; } = new()
    {
        [Standard] = WhisperModelState.Ready,
        [Accurate] = WhisperModelState.Missing,
    };

    public IReadOnlyList<WhisperModelStatus> GetModels() => [.. States.Keys.Select(model => GetStatus(model)!)];

    public WhisperModelStatus? GetStatus(string model) =>
        States.TryGetValue(model, out var state) ? new WhisperModelStatus(state, model, 0, null, null) : null;

    public WhisperDownloadStart? StartDownload(string model) => States.ContainsKey(model) ? WhisperDownloadStart.Started : null;
}
