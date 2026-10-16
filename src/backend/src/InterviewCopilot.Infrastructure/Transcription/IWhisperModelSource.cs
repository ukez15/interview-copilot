using System.Diagnostics.CodeAnalysis;
using Whisper.net.Ggml;

namespace InterviewCopilot.Infrastructure.Transcription;

/// <summary>Opens the bytes of a ggml model. Replaced by a fake in tests so they never hit the network.</summary>
internal interface IWhisperModelSource
{
    /// <summary>Opens the model stream; the caller disposes it.</summary>
    Task<Stream> OpenAsync(GgmlType type, CancellationToken cancellationToken);
}

/// <summary>Downloads the model from the public Whisper.net / Hugging Face location.</summary>
[ExcludeFromCodeCoverage(Justification = "Real network download; covered by the manual release checklist.")]
internal sealed class GgmlWhisperModelSource : IWhisperModelSource
{
    public Task<Stream> OpenAsync(GgmlType type, CancellationToken cancellationToken) =>
        WhisperGgmlDownloader.Default.GetGgmlModelAsync(type, cancellationToken: cancellationToken);
}
