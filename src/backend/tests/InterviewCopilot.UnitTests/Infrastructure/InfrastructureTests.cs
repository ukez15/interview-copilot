using System.Net;
using System.Text;
using FluentAssertions;
using InterviewCopilot.Application.Practice;
using InterviewCopilot.Application.Providers;
using InterviewCopilot.Application.Transcripts;
using InterviewCopilot.Domain.Enums;
using InterviewCopilot.Infrastructure.Documents;
using InterviewCopilot.Infrastructure.Llm;
using InterviewCopilot.Infrastructure.Pdf;
using InterviewCopilot.Infrastructure.Persistence;
using InterviewCopilot.Infrastructure.Security;
using InterviewCopilot.Infrastructure.Transcription;
using Microsoft.Extensions.Options;

namespace InterviewCopilot.UnitTests.Infrastructure;

public class InfrastructureTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Should_WriteValidWavHeader()
    {
        var pcm = new byte[] { 1, 2, 3, 4 };

        var wav = WavEncoder.Encode(pcm, 16_000);

        Encoding.ASCII.GetString(wav, 0, 4).Should().Be("RIFF");
        Encoding.ASCII.GetString(wav, 8, 4).Should().Be("WAVE");
        BitConverter.ToInt32(wav, 24).Should().Be(16_000);
        BitConverter.ToInt32(wav, 40).Should().Be(4);
        wav.Should().HaveCount(48);
    }

    [Fact]
    public void Should_RejectInvalidSampleRate()
    {
        var act = () => WavEncoder.Encode([], 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Should_StreamOllamaTokens_UntilDone()
    {
        const string body = """
            {"message":{"role":"assistant","content":"Hello"},"done":false}

            {"message":{"role":"assistant","content":" world"},"done":false}
            {"message":{"role":"assistant","content":""},"done":true}
            {"message":{"role":"assistant","content":"ignored"},"done":false}
            """;
        var handler = new StubHandler(HttpStatusCode.OK, body);
        var provider = new OllamaLlmProvider(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:11434/") });

        var tokens = new List<string>();
        await foreach (var token in provider.StreamAsync(Request(), Ct))
        {
            tokens.Add(token);
        }

        tokens.Should().Equal("Hello", " world");
        handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/api/chat");
        handler.LastBody.Should().Contain("\"stream\":true").And.Contain("\"role\":\"system\"").And.Contain("\"temperature\":0.3");
        provider.Name.Should().Be(ProviderNames.Ollama);
    }

    [Fact]
    public async Task Should_ExplainHowToPullModel_When_OllamaReturnsError()
    {
        var provider = new OllamaLlmProvider(new HttpClient(new StubHandler(HttpStatusCode.NotFound, "")) { BaseAddress = new Uri("http://x/") });

        var act = () => DrainAsync(provider);

        await act.Should().ThrowAsync<LlmUnavailableException>().WithMessage("*ollama pull llama3.2:3b*");
    }

    [Fact]
    public async Task Should_ExplainOllamaIsDown_When_ConnectionFails()
    {
        var provider = new OllamaLlmProvider(new HttpClient(new StubHandler(new HttpRequestException("refused"))) { BaseAddress = new Uri("http://x/") });

        var act = () => DrainAsync(provider);

        await act.Should().ThrowAsync<LlmUnavailableException>().WithMessage("*running*");
    }

    [Fact]
    public async Task Should_ExtractPlainTextAndMarkdown_Only()
    {
        var extractor = new PlainTextExtractor();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("# Resume"));

        extractor.CanExtract("notes.MD").Should().BeTrue();
        extractor.CanExtract("resume.pdf").Should().BeFalse();
        (await extractor.ExtractAsync("notes.md", stream, Ct)).Should().Be("# Resume");
    }

    [Fact]
    public void Should_HashAndVerifyPasswords()
    {
        var hasher = new AspNetPasswordHasher();

        var hash = hasher.Hash("correct horse");

        hash.Should().NotContain("correct horse");
        hasher.Verify(hash, "correct horse").Should().BeTrue();
        hasher.Verify(hash, "wrong").Should().BeFalse();
    }

    [Fact]
    public void Should_RenderPdf()
    {
        var transcript = new TranscriptDto(
            Guid.NewGuid(),
            "Acme",
            SessionMode.LiveInterview,
            DateTimeOffset.UtcNow,
            [new TranscriptEntryDto(1_000, Speaker.Interviewer, "Why Acme?"), new TranscriptEntryDto(3_000, Speaker.Candidate, "Because…")],
            [new AnswerEntryDto(DateTimeOffset.UtcNow, "Why Acme?", QuestionType.Experience, 1, "Suggested")],
            [new PracticeAttemptDto(Guid.NewGuid(), "Why C#?", "Because it is typed.", 8, "Good structure.", DateTimeOffset.UtcNow)]);

        var pdf = new QuestPdfTranscriptExporter().Export(transcript);
        var withoutPractice = new QuestPdfTranscriptExporter().Export(transcript with { Attempts = [] });
        var empty = new QuestPdfTranscriptExporter().Export(transcript with { Entries = [], Answers = [], Attempts = [] });

        Encoding.ASCII.GetString(pdf, 0, 4).Should().Be("%PDF");
        empty.Should().NotBeEmpty();
        pdf.Length.Should().BeGreaterThan(withoutPractice.Length); // the Practice section adds content
    }

    [Fact]
    public void Should_ResolveDataPaths()
    {
        var custom = new StorageOptions { DataDirectory = Path.Combine("x", "data") };
        var defaults = new StorageOptions();

        custom.ResolveDatabasePath().Should().Be(Path.Combine("x", "data", StorageOptions.DatabaseFileName));
        custom.ResolveModelsDirectory().Should().Be(Path.Combine("x", "data", "models"));
        defaults.ResolveDataDirectory().Should().EndWith(StorageOptions.AppFolderName);
    }

    [Fact]
    public void Should_KnowDefaultWhisperModel()
    {
        WhisperModelCatalog.TryGet("ggml-small.en.bin", out _).Should().BeTrue();
        WhisperModelCatalog.TryGet("unknown.bin", out _).Should().BeFalse();
    }

    private static async Task<int> DrainAsync(OllamaLlmProvider provider)
    {
        var count = 0;
        await foreach (var _ in provider.StreamAsync(Request(), Ct))
        {
            count++;
        }

        return count;
    }

    private static LlmRequest Request() =>
        new("llama3.2:3b", [new LlmMessage(LlmRole.System, "rules"), new LlmMessage(LlmRole.User, "q"), new LlmMessage(LlmRole.Assistant, "a")], 0.3);

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly Exception? _exception;

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public StubHandler(Exception exception)
            : this(HttpStatusCode.OK, string.Empty) => _exception = exception;

        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_exception is not null)
            {
                throw _exception;
            }

            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_status) { Content = new StringContent(_body) };
        }
    }
}
