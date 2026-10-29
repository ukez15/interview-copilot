using InterviewCopilot.Application.Providers;
using InterviewCopilot.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using Whisper.net.Ggml;

namespace InterviewCopilot.Infrastructure.Transcription;

/// <summary>
/// Owns the local Whisper model files (#85): the configured default and <see cref="WhisperModelCatalog.AccurateModel"/>.
/// Reports whether each is present and downloads one on request, one download at a time, through
/// "&lt;name&gt;.download" and an atomic rename. Models are named by file name.
/// </summary>
internal sealed class WhisperModelManager : IWhisperModelStatus, IDisposable
{
    private const int CopyBufferSize = 81_920;

    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly IOptions<TranscriptionOptions> _transcriptionOptions;
    private readonly IOptions<StorageOptions> _storageOptions;
    private readonly IWhisperModelSource _source;
    private readonly Dictionary<string, string> _failures = new(StringComparer.OrdinalIgnoreCase);

    private string? _downloading;
    private long _bytesDownloaded;
    private long? _totalBytes;
    private Task _download = Task.CompletedTask;

    public WhisperModelManager(
        IOptions<TranscriptionOptions> transcriptionOptions,
        IOptions<StorageOptions> storageOptions,
        IWhisperModelSource source)
    {
        ArgumentNullException.ThrowIfNull(transcriptionOptions);
        ArgumentNullException.ThrowIfNull(storageOptions);
        ArgumentNullException.ThrowIfNull(source);
        _transcriptionOptions = transcriptionOptions;
        _storageOptions = storageOptions;
        _source = source;
    }

    /// <summary>The configured model, used when a user hasn't chosen one.</summary>
    public string DefaultModel => _transcriptionOptions.Value.Providers.Whisper.Model;

    /// <summary>The in-flight (or last) download task; tests await it.</summary>
    internal Task CurrentDownload
    {
        get
        {
            lock (_gate)
            {
                return _download;
            }
        }
    }

    /// <summary>Full path of an offered model when it is on disk, otherwise null.</summary>
    public string? GetReadyPath(string model)
    {
        var offered = Offered(model);
        if (offered is null)
        {
            return null;
        }

        var path = ModelPath(offered);
        return File.Exists(path) ? path : null;
    }

    public IReadOnlyList<WhisperModelStatus> GetModels() => [.. OfferedModels().Select(StatusOf)];

    public WhisperModelStatus? GetStatus(string model) => Offered(model) is { } offered ? StatusOf(offered) : null;

    public WhisperDownloadStart? StartDownload(string model)
    {
        var offered = Offered(model);
        if (offered is null)
        {
            return null;
        }

        lock (_gate)
        {
            if (_downloading is not null)
            {
                return WhisperDownloadStart.AlreadyDownloading;
            }

            var path = ModelPath(offered);
            if (File.Exists(path))
            {
                return WhisperDownloadStart.AlreadyReady;
            }

            if (!WhisperModelCatalog.TryGet(offered, out var ggml))
            {
                _failures[offered] = $"Whisper model '{offered}' can't be downloaded automatically. Put the file in {_storageOptions.Value.ResolveModelsDirectory()}.";
                return WhisperDownloadStart.Started;
            }

            _downloading = offered;
            _bytesDownloaded = 0;
            _totalBytes = null;
            _failures.Remove(offered);
            _download = Task.Run(() => RunDownloadAsync(offered, ggml, path), CancellationToken.None);
            return WhisperDownloadStart.Started;
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort: a leftover .download file is overwritten by the next attempt.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above.
        }
    }

    private IEnumerable<string> OfferedModels() =>
        new[] { DefaultModel, WhisperModelCatalog.AccurateModel }.Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>The offered model's own spelling of the name, or null when it isn't offered.</summary>
    private string? Offered(string model) =>
        OfferedModels().FirstOrDefault(offered => string.Equals(offered, model, StringComparison.OrdinalIgnoreCase));

    private string ModelPath(string model) => Path.Combine(_storageOptions.Value.ResolveModelsDirectory(), model);

    private WhisperModelStatus StatusOf(string model)
    {
        var approximate = WhisperModelCatalog.TryGet(model, out var ggml) ? ggml.ApproximateBytes : null;
        lock (_gate)
        {
            if (string.Equals(_downloading, model, StringComparison.OrdinalIgnoreCase))
            {
                return new WhisperModelStatus(WhisperModelState.Downloading, model, _bytesDownloaded, _totalBytes, null, approximate);
            }

            if (File.Exists(ModelPath(model)))
            {
                return new WhisperModelStatus(WhisperModelState.Ready, model, 0, null, null, approximate);
            }

            return _failures.TryGetValue(model, out var failure)
                ? new WhisperModelStatus(WhisperModelState.Failed, model, 0, null, failure, approximate)
                : new WhisperModelStatus(WhisperModelState.Missing, model, 0, null, null, approximate);
        }
    }

    private async Task RunDownloadAsync(string model, GgmlModel ggml, string path)
    {
        var temporary = path + ".download";
        string? failure = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var source = await _source.OpenAsync(ggml.Type, ggml.Quantization, _shutdown.Token))
            await using (var target = File.Create(temporary))
            {
                if (source.CanSeek)
                {
                    lock (_gate)
                    {
                        _totalBytes = source.Length;
                    }
                }

                var buffer = new byte[CopyBufferSize];
                int read;
                while ((read = await source.ReadAsync(buffer, _shutdown.Token)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), _shutdown.Token);
                    lock (_gate)
                    {
                        _bytesDownloaded += read;
                    }
                }
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failure = ex is OperationCanceledException
                ? "The download was cancelled."
                : $"The download failed: {ex.Message}";
            TryDelete(temporary);
        }
        finally
        {
            lock (_gate)
            {
                _downloading = null;
                if (failure is not null)
                {
                    _failures[model] = failure;
                }
            }
        }
    }
}
