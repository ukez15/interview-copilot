namespace InterviewCopilot.Application.Providers;

public static class ProviderNames
{
    public const string Whisper = "whisper";
    public const string Deepgram = "deepgram";
    public const string Ollama = "ollama";
}

/// <summary>Bound from the "Transcription" config section.</summary>
public sealed class TranscriptionOptions
{
    public const string SectionName = "Transcription";

    public string Provider { get; set; } = ProviderNames.Whisper;

    public TranscriptionProvidersOptions Providers { get; set; } = new();
}

public sealed class TranscriptionProvidersOptions
{
    public WhisperOptions Whisper { get; set; } = new();

    public DeepgramOptions Deepgram { get; set; } = new();
}

public sealed class WhisperOptions
{
    /// <summary>ggml model file name; downloaded on request into the models folder.</summary>
    public string Model { get; set; } = "ggml-small.en.bin";

    public string Language { get; set; } = "en";
}

/// <summary>
/// Deepgram is implemented but disabled by default (zero-cost principle). Set Enabled=true to let
/// users pick it in Settings; they then supply their own key.
/// </summary>
public sealed class DeepgramOptions
{
    public bool Enabled { get; set; }

    public Uri BaseUrl { get; set; } = new("https://api.deepgram.com/");

    public string Model { get; set; } = "nova-3";
}

/// <summary>Bound from the "Llm" config section.</summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    public string Provider { get; set; } = ProviderNames.Ollama;

    public string Model { get; set; } = "llama3.2:3b";

    public double Temperature { get; set; } = 0.4;

    public OllamaOptions Ollama { get; set; } = new();
}

public sealed class OllamaOptions
{
    public Uri BaseUrl { get; set; } = new("http://127.0.0.1:11434/");
}
