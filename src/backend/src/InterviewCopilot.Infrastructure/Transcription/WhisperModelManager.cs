using InterviewCopilot.Application.Providers;
using InterviewCopilot.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using Whisper.net.Ggml;

namespace InterviewCopilot.Infrastructure.Transcription;

/// <summary>
/// Owns the local Whisper model file: reports whether it is present and downloads it on request,
/// one download at a time, through "&lt;name&gt;.download" and an atomic rename.
/// </summary>
internal sealed class WhisperModelManager : IWhisperModelStatus, IDisposable
{
    private const int CopyBufferSize = 81_920;

    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly IOptions<TranscriptionOptions> _transcriptionOptions;
    private readonly IOptions<StorageOptions> _storageOptions;
    private readonly IWhisperModelSource _source;

    private bool _downloading;
    private long _bytesDownloaded;
    private long? _totalBytes;
    private string? _failure;
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

    /// <summary>Full path of the model when it is on disk, otherwise null.</summary>
    public string? GetReadyPath()
    {
        var path = ModelPath();
        return File.Exists(path) ? path : null;
    }

    public WhisperModelStatus GetStatus()
    {
        var model = ModelName();
        lock (_gate)
        {
            if (_downloading)
            {
                return new WhisperModelStatus(WhisperModelState.Downloading, model, _bytesDownloaded, _totalBytes, null);
            }

            if (File.Exists(ModelPath()))
            {
                return new WhisperModelStatus(WhisperModelState.Ready, model, 0, null, null);
            }

            return _failure is null
                ? new WhisperModelStatus(WhisperModelState.Missing, model, 0, null, null)
                : new WhisperModelStatus(WhisperModelState.Failed, model, 0, null, _failure);
        }
    }

    public WhisperDownloadStart StartDownload()
    {
        var model = ModelName();
        lock (_gate)
        {
            if (_downloading)
            {
                return WhisperDownloadStart.AlreadyDownloading;
            }

            var path = ModelPath();
            if (File.Exists(path))
            {
                return WhisperDownloadStart.AlreadyReady;
            }

            if (!WhisperModelCatalog.TryGetType(model, out var type))
            {
                _failure = $"Whisper model '{model}' can't be downloaded automatically. Put the file in {_storageOptions.Value.ResolveModelsDirectory()}.";
                return WhisperDownloadStart.Started;
            }

            _downloading = true;
            _bytesDownloaded = 0;
            _totalBytes = null;
            _failure = null;
            _download = Task.Run(() => RunDownloadAsync(type, path), CancellationToken.None);
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

    private string ModelName() => _transcriptionOptions.Value.Providers.Whisper.Model;

    private string ModelPath() => Path.Combine(_storageOptions.Value.ResolveModelsDirectory(), ModelName());

    private async Task RunDownloadAsync(GgmlType type, string path)
    {
        var temporary = path + ".download";
        string? failure = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var source = await _source.OpenAsync(type, _shutdown.Token))
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
                _downloading = false;
                _failure = failure;
            }
        }
    }
}
