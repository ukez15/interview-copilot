using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using InterviewCopilot.Application.Documents;
using InterviewCopilot.Application.Sessions;

namespace InterviewCopilot.IntegrationTests;

public sealed class JobPostingApiTests : IClassFixture<ApiFactory>
{
    private const string Page = """
        <html><head><title>Careers</title>
        <script type="application/ld+json">
        {"@type":"JobPosting","title":"Backend Engineer","hiringOrganization":{"name":"Acme"},
         "description":"<p>Build APIs.</p><ul><li>C#</li></ul>"}
        </script></head><body></body></html>
        """;

    private static readonly object NewSession = new
    {
        Title = "Acme onsite",
        Mode = "LiveInterview",
        AnswerLength = "Short",
        Framework = "Star",
        PinnedQuestionType = "Behavioral",
        SessionPrompt = "Two senior engineers.",
    };

    private static readonly System.Text.Json.JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly ApiFactory _factory;

    public JobPostingApiTests(ApiFactory factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(HttpClient Client, SessionDto Session)> SignedInWithSessionAsync(string username)
    {
        var client = await _factory.CreateSignedInClientAsync(username);
        var session = await (await client.PostAsJsonAsync("/api/sessions", NewSession, Ct)).Content.ReadFromJsonAsync<SessionDto>(Json, Ct);
        return (client, session!);
    }

    private static string Route(Guid sessionId) => $"/api/sessions/{sessionId}/documents/from-link/preview";

    [Fact]
    public async Task Should_ReturnPreview_When_PageHasJsonLd()
    {
        _factory.Fetcher.Respond = url => new FetchedPage(url, Page);
        var (client, session) = await SignedInWithSessionAsync("jd-ok");

        var response = await client.PostAsJsonAsync(Route(session.Id), new { Url = "https://jobs.example.com/roles/1" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        body.RootElement.GetProperty("suggestedName").GetString().Should().Be("Backend Engineer - Acme.txt");
        body.RootElement.GetProperty("text").GetString().Should().Be("Backend Engineer\nAcme\n\nBuild APIs.\n\n- C#");
        body.RootElement.GetProperty("source").GetString().Should().Be("JsonLd");
        _factory.Fetcher.Requested.Should().Contain(new Uri("https://jobs.example.com/roles/1"));
    }

    [Fact]
    public async Task Should_StoreNothing_When_PreviewSucceeds()
    {
        _factory.Fetcher.Respond = url => new FetchedPage(url, Page);
        var (client, session) = await SignedInWithSessionAsync("jd-nostore");

        (await client.PostAsJsonAsync(Route(session.Id), new { Url = "https://jobs.example.com/roles/1" }, Ct)).EnsureSuccessStatusCode();

        var fetched = await client.GetFromJsonAsync<SessionDto>($"/api/sessions/{session.Id}", Json, Ct);
        fetched!.Documents.Should().BeEmpty();
    }

    [Fact]
    public async Task Should_Return400WithPasteInstead_When_PageHasNoJobDescription()
    {
        _factory.Fetcher.Respond = url => new FetchedPage(url, "<html><head><title>Sign in</title></head><body><div id=\"root\"></div></body></html>");
        var (client, session) = await SignedInWithSessionAsync("jd-empty");

        var response = await client.PostAsJsonAsync(Route(session.Id), new { Url = "https://jobs.example.com/wall" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        body.RootElement.GetProperty("detail").GetString()
            .Should().Be("We couldn't read a job description from that page. Paste the text instead.");
    }

    [Theory]
    [InlineData("file:///c:/x")]
    [InlineData("ftp://example.com/a")]
    [InlineData("not a url")]
    [InlineData("")]
    public async Task Should_Return400WithoutFetching_When_UrlIsNotHttp(string url)
    {
        var (client, session) = await SignedInWithSessionAsync("jd-badurl-" + Math.Abs(url.GetHashCode(StringComparison.Ordinal)));
        var before = _factory.Fetcher.Requested.Count;

        var response = await client.PostAsJsonAsync(Route(session.Id), new { Url = url }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        body.RootElement.GetProperty("detail").GetString().Should().Be("Enter a link that starts with http:// or https://.");
        _factory.Fetcher.Requested.Count.Should().Be(before);
    }

    [Fact]
    public async Task Should_Return400_When_BodyHasNoUrl()
    {
        var (client, session) = await SignedInWithSessionAsync("jd-nourl");

        var response = await client.PostAsJsonAsync(Route(session.Id), new { }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Should_Return401_When_NotSignedIn()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync(Route(Guid.NewGuid()), new { Url = "https://jobs.example.com/a" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Should_Return404_When_SessionBelongsToSomeoneElse()
    {
        var (_, session) = await SignedInWithSessionAsync("jd-owner");
        var stranger = await _factory.CreateSignedInClientAsync("jd-stranger");

        var response = await stranger.PostAsJsonAsync(Route(session.Id), new { Url = "https://jobs.example.com/a" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Should_Return400_When_SessionIsFull()
    {
        _factory.Fetcher.Respond = url => new FetchedPage(url, Page);
        var (client, session) = await SignedInWithSessionAsync("jd-full");
        for (var i = 0; i < 10; i++)
        {
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent("text"u8.ToArray()), "file", $"d{i}.txt");
            (await client.PostAsync($"/api/sessions/{session.Id}/documents", form, Ct)).EnsureSuccessStatusCode();
        }

        var response = await client.PostAsJsonAsync(Route(session.Id), new { Url = "https://jobs.example.com/a" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
