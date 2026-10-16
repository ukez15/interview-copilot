namespace InterviewCopilot.Application.Audio;

/// <summary>Voice-activity buffering settings, bound from the <c>Transcription:Vad</c> config section.</summary>
public sealed class VadOptions
{
    /// <summary>Config section these options are bound from.</summary>
    public const string SectionName = "Transcription:Vad";

    /// <summary>Length of one audio frame; timestamps are frames received per speaker times this.</summary>
    public const int FrameMs = 100;

    /// <summary>Frames quieter than this (RMS, dBFS) count as silence.</summary>
    public double SilenceThresholdDbfs { get; set; } = -40;

    /// <summary>A segment ends after this much continuous silence.</summary>
    public int PauseMs { get; set; } = 600;

    /// <summary>A segment is cut after this much audio even without a pause.</summary>
    public int MaxSegmentMs { get; set; } = 15_000;

    /// <summary>Speech shorter than this is ignored.</summary>
    public int MinSpeechMs { get; set; } = 300;

    /// <summary>Frames queued per session before the oldest are dropped.</summary>
    public int QueueFrames { get; set; } = 200;
}
