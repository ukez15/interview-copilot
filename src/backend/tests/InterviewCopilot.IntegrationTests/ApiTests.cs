using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using InterviewCopilot.Application.Providers;
using InterviewCopilot.Application.Sessions;
using InterviewCopilot.Application.Transcripts;
using InterviewCopilot.Domain.Enums;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace InterviewCopilot.IntegrationTests;

public sealed class ApiTests : IClassFixture<ApiFactory>
{
    private static readonly object NewSession = new
    {
        Title = "Acme onsite",
        Mode = "LiveInterview",
        AnswerLength = "Short",
        Framework = "Star",
        PinnedQuestionType = "Behavioral",
        SessionPrompt = "Two senior engineers.",
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly ApiFactory _factory;

    public ApiTests(ApiFactory factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_ReportHealthy()
    {
        var response = await _factory.CreateClient().GetAsync("/health", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Should_RejectAnonymousRequests()
    {
        var response = await _factory.CreateClient().GetAsync("/api/sessions", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Should_RequireAuthForStorageInfo()
    {
        var response = await _factory.CreateClient().GetAsync("/api/storage", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Should_ReturnConfiguredDataDirectory()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"ic-data-{Guid.NewGuid():N}");
        using var factory = _factory.WithWebHostBuilder(builder => builder.UseSetting("Storage:DataDirectory", folder));
        var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/auth/register", _factory.Credentials("storageuser"), Ct)).EnsureSuccessStatusCode();
        var signIn = await client.PostAsJsonAsync("/api/auth/sign-in", _factory.Credentials("storageuser"), Ct);
        var token = await signIn.Content.ReadFromJsonAsync<ApiFactory.TokenResponse>(Ct);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);

        var response = await client.GetAsync("/api/storage", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        body.RootElement.GetProperty("dataDirectory").GetString().Should().Be(folder);
    }

    [Fact]
    public async Task Should_RejectWrongSecret()
    {
        await _factory.CreateSignedInClientAsync("wrongpw");

        var response = await _factory.CreateClient().PostAsJsonAsync(
            "/api/auth/sign-in", _factory.Credentials("wrongpw", _factory.TestSecret + "-wrong"), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Should_CreateSession_AttachDocument_And_RunLifecycle()
    {
        var client = await _factory.CreateSignedInClientAsync("lifecycle");

        var created = await (await client.PostAsJsonAsync("/api/sessions", NewSession, Ct)).Content.ReadFromJsonAsync<SessionDto>(Json, Ct);
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes("Led a team of five at Acme."));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", "resume.txt");
        var upload = await client.PostAsync($"/api/sessions/{created!.Id}/documents", form, Ct);
        var started = await client.PostAsync($"/api/sessions/{created.Id}/start", null, Ct);
        var ended = await client.PostAsync($"/api/sessions/{created.Id}/end", null, Ct);
        var fetched = await client.GetFromJsonAsync<SessionDto>($"/api/sessions/{created.Id}", Json, Ct);
        var list = await client.GetFromJsonAsync<List<SessionDto>>("/api/sessions", Json, Ct);

        upload.StatusCode.Should().Be(HttpStatusCode.Created);
        started.StatusCode.Should().Be(HttpStatusCode.OK);
        ended.StatusCode.Should().Be(HttpStatusCode.OK);
        fetched!.Documents.Should().ContainSingle().Which.FileName.Should().Be("resume.txt");
        list.Should().ContainSingle();
    }

    [Fact]
    public async Task Should_CopySessionWithTenDocuments_And_HideItFromOtherUsers()
    {
        var client = await _factory.CreateSignedInClientAsync("copier");
        var stranger = await _factory.CreateSignedInClientAsync("copy-stranger");
        var source = await (await client.PostAsJsonAsync("/api/sessions", NewSession, Ct)).Content.ReadFromJsonAsync<SessionDto>(Json, Ct);
        for (var i = 0; i < 10; i++)
        {
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(Encoding.UTF8.GetBytes($"Document number {i}."));
            file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            form.Add(file, "file", $"doc{i}.txt");
            (await client.PostAsync($"/api/sessions/{source!.Id}/documents", form, Ct)).StatusCode.Should().Be(HttpStatusCode.Created);
        }

        var response = await client.PostAsJsonAsync(
            "/api/sessions",
            new { Title = "Zoom meeting – 16 Oct 14:05", Mode = "Conversation", AnswerLength = "Long", Framework = "None", SessionPrompt = "ignored", CopyFromSessionId = source!.Id },
            Ct);
        var copy = await response.Content.ReadFromJsonAsync<SessionDto>(Json, Ct);
        var denied = await stranger.PostAsJsonAsync(
            "/api/sessions",
            new { Title = "Stolen", Mode = "LiveInterview", AnswerLength = "Short", Framework = "None", CopyFromSessionId = source.Id },
            Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        copy!.Title.Should().Be("Zoom meeting – 16 Oct 14:05");
        copy.Mode.Should().Be(SessionMode.LiveInterview);
        copy.AnswerLength.Should().Be(AnswerLength.Short);
        copy.Framework.Should().Be(AnswerFramework.Star);
        copy.SessionPrompt.Should().Be("Two senior engineers.");
        copy.Documents.Should().HaveCount(10);
        denied.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Should_AttachPdfDocument_And_RejectCorruptPdf()
    {
        var client = await _factory.CreateSignedInClientAsync("pdfupload");
        var created = await (await client.PostAsJsonAsync("/api/sessions", NewSession, Ct)).Content.ReadFromJsonAsync<SessionDto>(Json, Ct);
        var builder = new PdfDocumentBuilder();
        builder.AddPage(595, 842).AddText("Led a team of five at Acme.", 12, new PdfPoint(25, 700), builder.AddStandard14Font(Standard14Font.Helvetica));

        var good = await client.PostAsync($"/api/sessions/{created!.Id}/documents", PdfForm(builder.Build(), "resume.pdf"), Ct);
        var bad = await client.PostAsync($"/api/sessions/{created.Id}/documents", PdfForm([1, 2, 3], "broken.pdf"), Ct);
        var fetched = await client.GetFromJsonAsync<SessionDto>($"/api/sessions/{created.Id}", Json, Ct);

        good.StatusCode.Should().Be(HttpStatusCode.Created);
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        fetched!.Documents.Should().ContainSingle().Which.FileName.Should().Be("resume.pdf");
    }

    private static MultipartFormDataContent PdfForm(byte[] bytes, string fileName)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", fileName);
        return form;
    }

    [Fact]
    public async Task Should_Return400_When_RuleBroken_And_404_ForOtherUsersSession()
    {
        var owner = await _factory.CreateSignedInClientAsync("owner");
        var stranger = await _factory.CreateSignedInClientAsync("stranger");
        var created = await (await owner.PostAsJsonAsync("/api/sessions", NewSession, Ct)).Content.ReadFromJsonAsync<SessionDto>(Json, Ct);

        var endDraft = await owner.PostAsync($"/api/sessions/{created!.Id}/end", null, Ct);
        var peek = await stranger.GetAsync($"/api/sessions/{created.Id}", Ct);

        endDraft.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        peek.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Should_StreamAnswer_AsServerSentEvents_And_SaveIt()
    {
        _factory.Llm.Fail = false;
        var client = await _factory.CreateSignedInClientAsync("streamer");
        var created = await (await client.PostAsJsonAsync("/api/sessions", NewSession, Ct)).Content.ReadFromJsonAsync<SessionDto>(Json, Ct);

        var response = await client.PostAsJsonAsync($"/api/sessions/{created!.Id}/answers", new { Question = "Tell me about a time you led a team." }, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        var transcript = await client.GetFromJsonAsync<TranscriptDto>($"/api/sessions/{created.Id}/transcript", Json, Ct);
        var pdf = await client.GetAsync($"/api/sessions/{created.Id}/transcript/pdf", Ct);

        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        body.Should().Contain("event: meta").And.Contain("event: delta").And.Contain("data: Suggested ").And.Contain("event: done");
        transcript!.Answers.Should().ContainSingle().Which.Text.Should().Be("Suggested answer.");
        pdf.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
    }

    [Fact]
    public async Task Should_StreamErrorEvent_When_ModelUnavailable()
    {
        var client = await _factory.CreateSignedInClientAsync("offline");
        var created = await (await client.PostAsJsonAsync("/api/sessions", NewSession, Ct)).Content.ReadFromJsonAsync<SessionDto>(Json, Ct);
        _factory.Llm.Fail = true;

        try
        {
            var body = await (await client.PostAsJsonAsync($"/api/sessions/{created!.Id}/answers", new { Question = "Why us?" }, Ct))
                .Content.ReadAsStringAsync(Ct);

            body.Should().Contain("event: error").And.Contain("Can't reach Ollama.");
        }
        finally
        {
            _factory.Llm.Fail = false;
        }
    }

    [Fact]
    public async Task Should_ListOnlyFreeProviders_ByDefault()
    {
        var client = await _factory.CreateSignedInClientAsync("settings");

        var body = await client.GetStringAsync("/api/providers", Ct);

        body.Should().Contain("whisper").And.Contain("ollama").And.NotContain("deepgram");
    }

    [Fact]
    public async Task Should_RequireAuth_ForWhisperModelEndpoints()
    {
        var anonymous = _factory.CreateClient();

        (await anonymous.GetAsync("/api/providers/whisper/models", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsync(DownloadUrl(FakeWhisperModel.Standard), null, Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.PutAsJsonAsync("/api/preferences/speech-model", new { Model = (string?)null }, Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Should_ReportWhisperModelState()
    {
        var client = await _factory.CreateSignedInClientAsync("whisper-state");
        _factory.WhisperModel.State = WhisperModelState.Missing;

        var body = await client.GetStringAsync("/api/providers/whisper/models", Ct);

        body.Should().StartWith("[").And.Contain("\"state\":\"missing\"").And.Contain(FakeWhisperModel.Standard)
            .And.Contain(FakeWhisperModel.Accurate).And.Contain("\"approximateBytes\":574000000");
    }

    private static string DownloadUrl(string model) => $"/api/providers/whisper/models/{model}/download";

    [Fact]
    public async Task Should_Return404_ForASpeechModelThatIsNotOffered()
    {
        var client = await _factory.CreateSignedInClientAsync("whisper-unknown");

        (await client.PostAsync(DownloadUrl("ggml-made-up.bin"), null, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Should_ChooseADownloadedSpeechModel_AndRefuseOneThatIsNot()
    {
        var client = await _factory.CreateSignedInClientAsync("whisper-choose");
        _factory.WhisperModel.AccurateState = WhisperModelState.Missing;

        var refused = await client.PutAsJsonAsync("/api/preferences/speech-model", new { Model = FakeWhisperModel.Accurate }, Ct);
        _factory.WhisperModel.AccurateState = WhisperModelState.Ready;
        var chosen = await client.PutAsJsonAsync("/api/preferences/speech-model", new { Model = FakeWhisperModel.Accurate }, Ct);

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        chosen.StatusCode.Should().Be(HttpStatusCode.OK);
        (await chosen.Content.ReadAsStringAsync(Ct)).Should().Contain($"\"speechModel\":\"{FakeWhisperModel.Accurate}\"");
        (await client.GetStringAsync("/api/preferences", Ct)).Should().Contain($"\"defaultSpeechModel\":\"{FakeWhisperModel.Standard}\"");
    }

    [Fact]
    public async Task Should_StartWhisperDownload_Then_Conflict_Then_OkWhenReady()
    {
        var client = await _factory.CreateSignedInClientAsync("whisper-download");
        _factory.WhisperModel.State = WhisperModelState.Missing;

        var started = await client.PostAsync(DownloadUrl(FakeWhisperModel.Standard), null, Ct);
        var again = await client.PostAsync(DownloadUrl(FakeWhisperModel.Standard), null, Ct);
        _factory.WhisperModel.State = WhisperModelState.Ready;
        var ready = await client.PostAsync(DownloadUrl(FakeWhisperModel.Standard), null, Ct);

        started.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await started.Content.ReadAsStringAsync(Ct)).Should().Contain("\"state\":\"downloading\"");
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        ready.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ready.Content.ReadAsStringAsync(Ct)).Should().Contain("\"state\":\"ready\"");
    }
}
