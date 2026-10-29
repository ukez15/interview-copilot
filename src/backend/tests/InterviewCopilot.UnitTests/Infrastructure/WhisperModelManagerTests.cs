using FluentAssertions;
using InterviewCopilot.Application.Providers;
using InterviewCopilot.Domain.Enums;
using InterviewCopilot.Infrastructure.Persistence;
using InterviewCopilot.Infrastructure.Transcription;
using Microsoft.Extensions.Options;
using Whisper.net.Ggml;

namespace InterviewCopilot.UnitTests.Infrastructure;

public sealed class WhisperModelManagerTests : IDisposable
{
    private const string ModelFile = "ggml-tiny.en.bin";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ic-whisper-{Guid.NewGuid():N}");
    private readonly FakeSource _source = new();
    private readonly WhisperModelManager _manager;

    public WhisperModelManagerTests()
    {
        _manager = Create(ModelFile);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string ModelPath => Path.Combine(_root, "models", ModelFile);

    public void Dispose()
    {
        _manager.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Should_OfferTheConfiguredModel_AndTheMoreAccurateOne_AndNothingElse()
    {
        _manager.GetModels().Select(m => m.Model).Should().Equal(ModelFile, WhisperModelCatalog.AccurateModel);
        _manager.GetStatus(WhisperModelCatalog.AccurateModel)!.ApproximateBytes.Should().BePositive();

        _manager.GetStatus("ggml-base.en.bin").Should().BeNull();
        _manager.StartDownload("ggml-base.en.bin").Should().BeNull();
        _manager.GetReadyPath("ggml-base.en.bin").Should().BeNull();
        _source.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Should_DownloadTheMoreAccurateModel_Quantized_NextToTheDefault()
    {
        _manager.StartDownload(WhisperModelCatalog.AccurateModel).Should().Be(WhisperDownloadStart.Started);
        await _manager.CurrentDownload.WaitAsync(Ct);

        _source.Requested.Should().Be((GgmlType.LargeV3Turbo, QuantizationType.Q5_0));
        _manager.GetReadyPath(WhisperModelCatalog.AccurateModel).Should().Be(Path.Combine(_root, "models", WhisperModelCatalog.AccurateModel));
        _manager.GetStatus(ModelFile)!.State.Should().Be(WhisperModelState.Missing, "each model has its own file");
    }

    [Fact]
    public async Task Should_DownloadOneModelAtATime()
    {
        _source.Gate = new TaskCompletionSource();
        _manager.StartDownload(ModelFile).Should().Be(WhisperDownloadStart.Started);

        _manager.StartDownload(WhisperModelCatalog.AccurateModel).Should().Be(WhisperDownloadStart.AlreadyDownloading);
        _manager.GetStatus(WhisperModelCatalog.AccurateModel)!.State.Should().Be(WhisperModelState.Missing);

        _source.Gate.SetResult();
        await _manager.CurrentDownload.WaitAsync(Ct);
        _source.Calls.Should().Be(1);
    }

    [Fact]
    public void Should_BeMissing_When_NoFileAndNoDownload()
    {
        var status = _manager.GetStatus(ModelFile)!;

        status.State.Should().Be(WhisperModelState.Missing);
        status.Model.Should().Be(ModelFile);
        _manager.GetReadyPath(ModelFile).Should().BeNull();
    }

    [Fact]
    public void Should_BeReady_When_FileAlreadyPresent()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
        File.WriteAllText(ModelPath, "model");

        _manager.GetStatus(ModelFile)!.State.Should().Be(WhisperModelState.Ready);
        _manager.StartDownload(ModelFile).Should().Be(WhisperDownloadStart.AlreadyReady);
        _manager.GetReadyPath(ModelFile).Should().Be(ModelPath);
        _source.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Should_MoveFromMissingToDownloadingToReady()
    {
        _source.Gate = new TaskCompletionSource();
        _source.Payload = new byte[1000];

        _manager.StartDownload(ModelFile).Should().Be(WhisperDownloadStart.Started);
        _manager.GetStatus(ModelFile)!.State.Should().Be(WhisperModelState.Downloading);

        _source.Gate.SetResult();
        await _manager.CurrentDownload.WaitAsync(Ct);

        var status = _manager.GetStatus(ModelFile)!;
        status.State.Should().Be(WhisperModelState.Ready);
        (await File.ReadAllBytesAsync(ModelPath, Ct)).Should().HaveCount(1000);
        File.Exists(ModelPath + ".download").Should().BeFalse();
    }

    [Fact]
    public async Task Should_ReportBytesAndTotal_While_Downloading()
    {
        _source.Payload = new byte[300_000];
        _source.PauseAfterBytes = 100_000;
        _source.Seekable = true;

        _manager.StartDownload(ModelFile);
        await _source.Paused.Task.WaitAsync(Ct);
        await WaitUntilAsync(() => _manager.GetStatus(ModelFile)!.BytesDownloaded >= 100_000);

        var status = _manager.GetStatus(ModelFile)!;
        status.State.Should().Be(WhisperModelState.Downloading);
        status.BytesDownloaded.Should().BeGreaterThanOrEqualTo(100_000);
        status.TotalBytes.Should().Be(300_000);

        _source.Resume.SetResult();
        await _manager.CurrentDownload.WaitAsync(Ct);
        _manager.GetStatus(ModelFile)!.State.Should().Be(WhisperModelState.Ready);
    }

    [Fact]
    public async Task Should_IgnoreSecondStart_While_Downloading()
    {
        _source.Gate = new TaskCompletionSource();

        _manager.StartDownload(ModelFile).Should().Be(WhisperDownloadStart.Started);
        _manager.StartDownload(ModelFile).Should().Be(WhisperDownloadStart.AlreadyDownloading);

        _source.Gate.SetResult();
        await _manager.CurrentDownload.WaitAsync(Ct);
        _source.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Should_BeFailed_And_LeaveNoFiles_When_OpenThrows()
    {
        _source.Failure = new HttpRequestException("network down");

        _manager.StartDownload(ModelFile);
        await _manager.CurrentDownload.WaitAsync(Ct);

        var status = _manager.GetStatus(ModelFile)!;
        status.State.Should().Be(WhisperModelState.Failed);
        status.Message.Should().Contain("network down");
        File.Exists(ModelPath).Should().BeFalse();
        File.Exists(ModelPath + ".download").Should().BeFalse();
    }

    [Fact]
    public async Task Should_RemoveTemporaryFile_When_StreamFailsMidway()
    {
        _source.Payload = new byte[50_000];
        _source.FailAfterBytes = 20_000;

        _manager.StartDownload(ModelFile);
        await _manager.CurrentDownload.WaitAsync(Ct);

        _manager.GetStatus(ModelFile)!.State.Should().Be(WhisperModelState.Failed);
        File.Exists(ModelPath).Should().BeFalse();
        File.Exists(ModelPath + ".download").Should().BeFalse();
    }

    [Fact]
    public async Task Should_AllowRetry_After_Failure()
    {
        _source.Failure = new HttpRequestException("boom");
        _manager.StartDownload(ModelFile);
        await _manager.CurrentDownload.WaitAsync(Ct);

        _source.Failure = null;
        _manager.StartDownload(ModelFile).Should().Be(WhisperDownloadStart.Started);
        await _manager.CurrentDownload.WaitAsync(Ct);

        _manager.GetStatus(ModelFile)!.State.Should().Be(WhisperModelState.Ready);
    }

    [Fact]
    public void Should_BeFailed_When_ModelIsNotInCatalog()
    {
        using var manager = Create("custom-model.bin");

        manager.StartDownload("custom-model.bin");

        var status = manager.GetStatus("custom-model.bin")!;
        status.State.Should().Be(WhisperModelState.Failed);
        status.Message.Should().Contain("custom-model.bin");
        _source.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Should_RefuseToTranscribe_When_ModelNotReady()
    {
        using var provider = new WhisperSpeechToTextProvider(
            Options.Create(new TranscriptionOptions { Providers = new TranscriptionProvidersOptions { Whisper = new WhisperOptions { Model = ModelFile } } }),
            _manager);
        var chunk = new AudioChunk(Speaker.Interviewer, new byte[640], 16_000, 0);

        var act = () => provider.TranscribeAsync(chunk, null, Ct);

        (await act.Should().ThrowAsync<TranscriptionUnavailableException>())
            .WithMessage("*Download it in Settings*");
        _source.Calls.Should().Be(0);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, Ct);
        }
    }

    private WhisperModelManager Create(string model) => new(
        Options.Create(new TranscriptionOptions { Providers = new TranscriptionProvidersOptions { Whisper = new WhisperOptions { Model = model } } }),
        Options.Create(new StorageOptions { DataDirectory = _root }),
        _source);

    private sealed class FakeSource : IWhisperModelSource
    {
        private int _calls;

        public int Calls => _calls;

        public TaskCompletionSource? Gate { get; set; }

        public byte[] Payload { get; set; } = new byte[10];

        public Exception? Failure { get; set; }

        public int PauseAfterBytes { get; set; }

        public int FailAfterBytes { get; set; }

        public bool Seekable { get; set; }

        public TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public (GgmlType Type, QuantizationType Quantization)? Requested { get; private set; }

        public async Task<Stream> OpenAsync(GgmlType type, QuantizationType quantization, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            Requested = (type, quantization);
            if (Gate is not null)
            {
                await Gate.Task.WaitAsync(cancellationToken);
            }

            if (Failure is not null)
            {
                throw Failure;
            }

            return new ScriptedStream(this);
        }
    }

    private sealed class ScriptedStream(FakeSource script) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => script.Seekable;

        public override bool CanWrite => false;

        public override long Length => script.Seekable ? script.Payload.Length : throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (script.PauseAfterBytes > 0 && _position >= script.PauseAfterBytes && !script.Resume.Task.IsCompleted)
            {
                script.Paused.TrySetResult();
                await script.Resume.Task.WaitAsync(cancellationToken);
            }

            if (script.FailAfterBytes > 0 && _position >= script.FailAfterBytes)
            {
                throw new IOException("connection reset");
            }

            var limit = script.Payload.Length;
            if (script.PauseAfterBytes > 0 && _position < script.PauseAfterBytes)
            {
                limit = script.PauseAfterBytes;
            }
            else if (script.FailAfterBytes > 0 && _position < script.FailAfterBytes)
            {
                limit = script.FailAfterBytes;
            }

            var count = Math.Min(Math.Min(buffer.Length, 10_000), limit - _position);
            if (count <= 0)
            {
                return 0;
            }

            script.Payload.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
