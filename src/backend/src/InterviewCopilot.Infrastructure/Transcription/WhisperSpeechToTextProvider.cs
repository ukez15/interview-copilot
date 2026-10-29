using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using InterviewCopilot.Application.Providers;
using Microsoft.Extensions.Options;
using Whisper.net;

namespace InterviewCopilot.Infrastructure.Transcription;

/// <summary>
/// Local, free speech-to-text with whisper.cpp via Whisper.net (default, NFR-01). Models are downloaded by
/// <see cref="WhisperModelManager"/> (only when the user asks); each one loads the first time a chunk asks for it
/// and then stays loaded (#85).
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Needs native whisper.cpp + a ~500 MB model; covered by the manual release checklist.")]
internal sealed class WhisperSpeechToTextProvider(
    IOptions<TranscriptionOptions> transcriptionOptions,
    WhisperModelManager modelManager) : ISpeechToTextProvider, IDisposable
{
    internal const string ModelMissingMessage = "The speech model isn't downloaded yet. Download it in Settings → Speech model.";

    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly ConcurrentDictionary<string, WhisperFactory> _factories = new(StringComparer.OrdinalIgnoreCase);

    public string Name => ProviderNames.Whisper;

    public async Task<TranscriptionResult> TranscribeAsync(
        AudioChunk chunk,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        var factory = await GetFactoryAsync(chunk.Model ?? modelManager.DefaultModel, cancellationToken);
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
        foreach (var factory in _factories.Values)
        {
            factory.Dispose();
        }

        _loadLock.Dispose();
    }

    private async Task<WhisperFactory> GetFactoryAsync(string model, CancellationToken cancellationToken)
    {
        if (_factories.TryGetValue(model, out var loaded))
        {
            return loaded;
        }

        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            if (!_factories.TryGetValue(model, out loaded))
            {
                var path = modelManager.GetReadyPath(model)
                    ?? throw new TranscriptionUnavailableException(ModelMissingMessage);
                loaded = WhisperFactory.FromPath(path);
                _factories[model] = loaded;
            }

            return loaded;
        }
        finally
        {
            _loadLock.Release();
        }
    }
}
