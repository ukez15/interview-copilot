using InterviewCopilot.Domain.Enums;

namespace InterviewCopilot.Application.Audio;

/// <summary>
/// Decides whether a frame is worth transcribing at all (#78). A mock interview only needs the candidate's
/// speech while a question is open; dropping the rest at the sink keeps Whisper off the CPU while the model
/// writes the next question or scores an answer.
/// </summary>
public interface IAudioCaptureGate
{
    /// <summary>True when the frame should be queued for transcription.</summary>
    /// <param name="sessionId">The session the frame belongs to.</param>
    /// <param name="speaker">Whose audio it is.</param>
    /// <param name="cancellationToken">Cancels a lookup the decision needs.</param>
    /// <returns>True to transcribe, false to drop the frame.</returns>
    ValueTask<bool> AcceptsAsync(Guid sessionId, Speaker speaker, CancellationToken cancellationToken);
}

/// <summary>The gate for sinks that don't filter: every frame is transcribed.</summary>
public sealed class OpenAudioCaptureGate : IAudioCaptureGate
{
    /// <summary>The shared instance.</summary>
    public static readonly OpenAudioCaptureGate Instance = new();

    private OpenAudioCaptureGate()
    {
    }

    /// <inheritdoc />
    public ValueTask<bool> AcceptsAsync(Guid sessionId, Speaker speaker, CancellationToken cancellationToken) => ValueTask.FromResult(true);
}

/// <summary>
/// Where each speaker's audio stream is now, on the same clock as <see cref="SpeechSegment.StartMs"/>: frames
/// received since the stream began times <see cref="VadOptions.FrameMs"/>, counting frames the gate dropped.
/// </summary>
public interface IAudioStreamClock
{
    /// <summary>The position of the next frame in the speaker's stream, in milliseconds.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="speaker">Whose stream.</param>
    /// <returns>Milliseconds of audio received so far; 0 before the first frame.</returns>
    long PositionMs(Guid sessionId, Speaker speaker);

    /// <summary>
    /// How far transcription has caught up (#78): speech that started before this position has been transcribed
    /// and handed to listeners. Equals <see cref="PositionMs"/> when no audio is waiting to be transcribed here.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="speaker">Whose stream.</param>
    /// <returns>A position in milliseconds, at most <see cref="PositionMs"/>.</returns>
    long TranscribedThroughMs(Guid sessionId, Speaker speaker);
}
