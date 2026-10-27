using System.Buffers.Binary;

namespace InterviewCopilot.Application.Audio;

/// <summary>One stretch of speech cut out of a speaker's stream.</summary>
/// <param name="StartMs">Start of the first voiced frame: its position in the speaker's stream (frame index times 100 ms).</param>
/// <param name="EndMs">End of the last voiced frame.</param>
/// <param name="Pcm16">16 kHz mono PCM16 LE audio for the segment, trailing silence removed.</param>
public sealed record SpeechSegment(long StartMs, long EndMs, byte[] Pcm16);

/// <summary>
/// Buffers one speaker's frames and cuts them into speech segments using frame energy: a segment ends after a
/// pause or when it reaches the maximum length, and speech that is too short is discarded. Pure and not
/// thread-safe: use one instance per session and speaker.
/// </summary>
public sealed class VoiceActivityBuffer
{
    private readonly VadOptions _options;
    private readonly List<ReadOnlyMemory<byte>> _frames = [];
    private long _frameIndex;
    private long _startMs;
    private int _trailingSilentFrames;
    private long _lastVoiceEndMs;

    /// <summary>Creates a buffer with the given thresholds.</summary>
    /// <param name="options">Silence threshold, pause, maximum and minimum lengths.</param>
    public VoiceActivityBuffer(VadOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>The stream index the next frame is expected to have; another index means frames were skipped.</summary>
    public long NextFrameIndex => _frameIndex;

    /// <summary>True while speech is buffered and not yet cut into a segment.</summary>
    public bool HasSpeech => _frames.Count > 0;

    /// <summary>Where the last voiced frame ended, in stream milliseconds; 0 before any voice (#78).</summary>
    public long LastVoiceEndMs => _lastVoiceEndMs;

    /// <summary>Everything before this stream position (ms) has been cut into segments or found to be silence.</summary>
    public long CutThroughMs => HasSpeech ? _startMs : _frameIndex * VadOptions.FrameMs;

    /// <summary>Adds the next 100 ms frame. The buffer keeps a reference, so the caller must not change it afterwards.</summary>
    /// <param name="pcm16">One frame of PCM16 little-endian samples.</param>
    /// <returns>A finished segment, or null when nothing ended on this frame.</returns>
    public SpeechSegment? Push(ReadOnlyMemory<byte> pcm16) => Push(pcm16, _frameIndex);

    /// <summary>
    /// Adds a frame at a known position in the stream, so segment times stay true when earlier frames were
    /// skipped. Call <see cref="Flush"/> first if the index isn't <see cref="NextFrameIndex"/> while speech is buffered.
    /// </summary>
    /// <param name="pcm16">One frame of PCM16 little-endian samples.</param>
    /// <param name="frameIndex">The frame's position in the speaker's stream (0 for the first frame).</param>
    /// <returns>A finished segment, or null when nothing ended on this frame.</returns>
    public SpeechSegment? Push(ReadOnlyMemory<byte> pcm16, long frameIndex)
    {
        var atMs = frameIndex * VadOptions.FrameMs;
        _frameIndex = frameIndex + 1;
        var voiced = IsVoiced(pcm16.Span);
        if (voiced)
        {
            _lastVoiceEndMs = atMs + VadOptions.FrameMs;
        }

        if (_frames.Count == 0)
        {
            if (!voiced)
            {
                return null;
            }

            _startMs = atMs;
        }

        _frames.Add(pcm16);
        _trailingSilentFrames = voiced ? 0 : _trailingSilentFrames + 1;

        var pauseReached = _trailingSilentFrames * VadOptions.FrameMs >= _options.PauseMs;
        var capReached = _frames.Count * VadOptions.FrameMs >= _options.MaxSegmentMs;
        return pauseReached || capReached ? Finish() : null;
    }

    /// <summary>
    /// Ends the speech in progress because the stream is over. Trailing silence is trimmed and speech shorter than
    /// the minimum is dropped, as for a normal cut. The buffer is empty afterwards.
    /// </summary>
    /// <returns>The final segment, or null when nothing worth transcribing is buffered.</returns>
    public SpeechSegment? Flush() => Finish();

    private SpeechSegment? Finish()
    {
        var voicedFrames = _frames.Count - _trailingSilentFrames;
        var voicedMs = voicedFrames * VadOptions.FrameMs;
        SpeechSegment? segment = null;
        if (voicedMs >= _options.MinSpeechMs)
        {
            var pcm = new byte[_frames.Take(voicedFrames).Sum(frame => frame.Length)];
            var offset = 0;
            foreach (var frame in _frames.Take(voicedFrames))
            {
                frame.CopyTo(pcm.AsMemory(offset));
                offset += frame.Length;
            }

            segment = new SpeechSegment(_startMs, _startMs + voicedMs, pcm);
        }

        _frames.Clear();
        _trailingSilentFrames = 0;
        return segment;
    }

    private bool IsVoiced(ReadOnlySpan<byte> pcm16)
    {
        var samples = pcm16.Length / 2;
        if (samples == 0)
        {
            return false;
        }

        double sumOfSquares = 0;
        for (var i = 0; i < samples; i++)
        {
            double sample = BinaryPrimitives.ReadInt16LittleEndian(pcm16[(i * 2)..]);
            sumOfSquares += sample * sample;
        }

        var rms = Math.Sqrt(sumOfSquares / samples);
        var dbfs = 20 * Math.Log10(rms / 32_768d);
        return dbfs > _options.SilenceThresholdDbfs;
    }
}
