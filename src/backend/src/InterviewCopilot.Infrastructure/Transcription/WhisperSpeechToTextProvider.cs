using System.Diagnostics.CodeAnalysis;
using System.Text;
using InterviewCopilot.Application.Providers;
using Microsoft.Extensions.Options;
using Whisper.net;

namespace InterviewCopilot.Infrastructure.Transcription;

/// <summary>
/// Local, free speech-to-text with whisper.cpp via Whisper.net (default, NFR-01). The model is
/// downloaded by <see cref="WhisperModelManager"/> (only when the user asks) and loaded lazily.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Needs native whisper.cpp + a ~500 MB model; covered by the manual release checklist.")]
internal sealed class WhisperSpeechToTextProvider(
    IOptions<TranscriptionOptions> transcriptionOptions,
    WhisperModelManager modelManager) : ISpeechToTextProvider, IDisposable
{
    internal const string ModelMissingMessage = "The speech model isn't downloaded yet. Download it in Settings → Speech model.";

    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private WhisperFactory? _factory;

    public string Name => ProviderNames.Whisper;

    public async Task<TranscriptionResult> TranscribeAsync(
        AudioChunk chunk,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        var factory = await GetFactoryAsync(cancellationToken);
        var options = transcriptionOptions.Value.Providers.Whisper;

        await using var processor = factory.CreateBuilder().WithLanguage(options.Language).Build();
        using var wav = new MemoryStream(WavEncoder.Encode(chunk.Pcm16.Span, chunk.SampleRate));
        var text = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(wav, cancellationToken))
        {
            text.Append(segment.Text);
        }

        return new TranscriptionResult(text.ToString().Trim());
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _loadLock.Dispose();
    }

    private async Task<WhisperFactory> GetFactoryAsync(CancellationToken cancellationToken)
    {
        if (_factory is not null)
        {
            return _factory;
        }

        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            if (_factory is null)
            {
                var path = modelManager.GetReadyPath()
                    ?? throw new TranscriptionUnavailableException(ModelMissingMessage);
                _factory = WhisperFactory.FromPath(path);
            }

            return _factory;
        }
        finally
        {
            _loadLock.Release();
        }
    }
}
