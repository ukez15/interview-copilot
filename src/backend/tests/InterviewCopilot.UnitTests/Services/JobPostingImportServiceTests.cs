using FluentAssertions;
using InterviewCopilot.Application.Common.Exceptions;
using InterviewCopilot.Application.Documents;
using InterviewCopilot.Application.Sessions;
using InterviewCopilot.Domain;
using InterviewCopilot.Domain.Entities;
using InterviewCopilot.Domain.Enums;
using InterviewCopilot.Domain.Exceptions;

namespace InterviewCopilot.UnitTests.Services;

public class JobPostingImportServiceTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Uri Final = new("https://jobs.example.com/roles/42");
    private readonly InMemoryStore _store = new();
    private readonly FixedClock _clock = new();
    private readonly StubFetcher _fetcher = new();
    private readonly StubExtractor _extractor = new();
    private readonly JobPostingImportService _service;
    private readonly Session _session;

    public JobPostingImportServiceTests()
    {
        _service = new JobPostingImportService(new SessionService(_store, _store, _clock), _fetcher, _extractor);
        _session = Session.Create(Owner, "Acme", SessionMode.LiveInterview, _clock.GetUtcNow());
        _store.Add(_session);
        _extractor.Result = new ExtractedJobPosting("Backend Engineer", "Acme", "Careers", "Build APIs.", JobPostingSource.JsonLd);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<JobPostingPreviewDto> PreviewAsync(string url = "https://jobs.example.com/roles/42") =>
        _service.PreviewAsync(Owner, _session.Id, url, Ct);

    [Fact]
    public async Task Should_ReturnPreview_When_PageHasJobPosting()
    {
        var preview = await PreviewAsync();

        preview.Should().Be(new JobPostingPreviewDto("Backend Engineer - Acme.txt", "Build APIs.", JobPostingSource.JsonLd));
        _fetcher.Requested.Should().Be(new Uri("https://jobs.example.com/roles/42"));
        _store.SaveCount.Should().Be(0);
        _session.Documents.Should().BeEmpty();
    }

    [Fact]
    public async Task Should_NotFindSession_When_OwnedByAnotherUser()
    {
        var act = () => _service.PreviewAsync(Guid.NewGuid(), _session.Id, "https://jobs.example.com/a", Ct);

        await act.Should().ThrowAsync<NotFoundException>();
        _fetcher.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Should_RefuseBeforeFetching_When_SessionHasMaxDocuments()
    {
        for (var i = 0; i < SessionLimits.MaxDocumentsPerSession; i++)
        {
            _session.AttachDocument($"d{i}.txt", "x", _clock.GetUtcNow());
        }

        var act = () => PreviewAsync();

        (await act.Should().ThrowAsync<DomainException>())
            .WithMessage($"A session can have at most {SessionLimits.MaxDocumentsPerSession} documents. Remove one first.");
        _fetcher.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("jobs.example.com/role")]
    [InlineData("/relative/path")]
    [InlineData("file:///c:/x")]
    [InlineData("ftp://example.com/job")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:pass@jobs.example.com/role")]
    [InlineData("https://user@jobs.example.com/role")]
    public async Task Should_RejectUrlBeforeFetching_When_NotPlainHttpOrHttps(string url)
    {
        var act = () => PreviewAsync(url);

        (await act.Should().ThrowAsync<DomainException>()).WithMessage("Enter a link that starts with http:// or https://.");
        _fetcher.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Should_TrimUrl_When_SurroundedByWhitespace()
    {
        await PreviewAsync("  http://jobs.example.com/a  ");

        _fetcher.Requested.Should().Be(new Uri("http://jobs.example.com/a"));
    }

    [Fact]
    public async Task Should_ThrowPasteInstead_When_ExtractorFindsNothing()
    {
        _extractor.Result = null;

        var act = () => PreviewAsync();

        (await act.Should().ThrowAsync<DomainException>())
            .WithMessage("We couldn't read a job description from that page. Paste the text instead.");
    }

    [Fact]
    public async Task Should_ThrowPasteInstead_When_ExtractedTextIsBlank()
    {
        _extractor.Result = new ExtractedJobPosting("T", "C", null, "   ", JobPostingSource.Readable);

        var act = () => PreviewAsync();

        await act.Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task Should_PassFetchFailuresThrough()
    {
        _fetcher.Failure = new DomainException("That page took too long to respond. Paste the text instead.");

        var act = () => PreviewAsync();

        (await act.Should().ThrowAsync<DomainException>()).WithMessage("That page took too long to respond. Paste the text instead.");
    }

    [Theory]
    [InlineData("Backend Engineer", "Acme", "Page", "Backend Engineer - Acme.txt")]
    [InlineData("Backend Engineer", null, "Page", "Backend Engineer.txt")]
    [InlineData("Backend Engineer", "  ", "Page", "Backend Engineer.txt")]
    [InlineData(null, "Acme", "Careers at Acme", "Careers at Acme.txt")]
    [InlineData(null, null, "Careers at Acme", "Careers at Acme.txt")]
    [InlineData(null, null, null, "jobs.example.com.txt")]
    [InlineData("  ", null, "", "jobs.example.com.txt")]
    public async Task Should_NameDocument_FromTitleCompanyPageTitleOrHost(string? title, string? company, string? pageTitle, string expected)
    {
        _extractor.Result = new ExtractedJobPosting(title, company, pageTitle, "text", JobPostingSource.Readable);

        var preview = await PreviewAsync();

        preview.SuggestedName.Should().Be(expected);
        preview.Source.Should().Be(JobPostingSource.Readable);
    }

    [Fact]
    public async Task Should_UseFinalHost_When_PageRedirected()
    {
        _fetcher.Page = new FetchedPage(new Uri("https://careers.other.example/x"), "<html/>");
        _extractor.Result = new ExtractedJobPosting(null, null, null, "text", JobPostingSource.Readable);

        var preview = await PreviewAsync();

        preview.SuggestedName.Should().Be("careers.other.example.txt");
    }

    [Fact]
    public async Task Should_StripInvalidFileNameCharactersAndCollapseWhitespace()
    {
        _extractor.Result = new ExtractedJobPosting(
            "  Sr.\tEngineer: \"Platform\" <Core>\n/ Data|Infra?*  ", "A\\C\u0007me  Inc.", null, "text", JobPostingSource.JsonLd);

        var preview = await PreviewAsync();

        preview.SuggestedName.Should().Be("Sr. Engineer Platform Core DataInfra - ACme Inc.txt");
        preview.SuggestedName.IndexOfAny(Path.GetInvalidFileNameChars()).Should().Be(-1);
    }

    [Fact]
    public async Task Should_FallBack_When_TitleHasOnlyInvalidCharacters()
    {
        _extractor.Result = new ExtractedJobPosting("???", "***", "Real page title", "text", JobPostingSource.Readable);

        var preview = await PreviewAsync();

        preview.SuggestedName.Should().Be("Real page title.txt");
    }

    [Fact]
    public async Task Should_FallBackToHost_When_TitleAndPageTitleHaveOnlyInvalidCharacters()
    {
        _fetcher.Page = new FetchedPage(new Uri("https://jobs.example.com/"), "<html/>");
        _extractor.Result = new ExtractedJobPosting("???", null, "***", "text", JobPostingSource.Readable);

        var preview = await PreviewAsync();

        preview.SuggestedName.Should().Be("jobs.example.com.txt");
    }

    [Fact]
    public async Task Should_KeepNameWithinMaxLength_And_KeepExtension()
    {
        _extractor.Result = new ExtractedJobPosting(new string('a', 400), "Acme", null, "text", JobPostingSource.JsonLd);

        var preview = await PreviewAsync();

        preview.SuggestedName.Length.Should().Be(SessionLimits.MaxFileNameLength);
        preview.SuggestedName.Should().EndWith(".txt");
        preview.SuggestedName.Should().StartWith("aaaa");
    }

    [Fact]
    public async Task Should_NotSplitSurrogatePair_When_TruncatingName()
    {
        // 250 chars then an emoji (2 UTF-16 units) straddling the 251-char stem limit.
        _extractor.Result = new ExtractedJobPosting(new string('a', 250) + "\U0001F600\U0001F600", null, null, "text", JobPostingSource.JsonLd);

        var preview = await PreviewAsync();

        preview.SuggestedName.Length.Should().BeLessThanOrEqualTo(SessionLimits.MaxFileNameLength);
        preview.SuggestedName.Should().EndWith(".txt");
        var stem = preview.SuggestedName[..^4];
        char.IsHighSurrogate(stem[^1]).Should().BeFalse();
    }

    [Fact]
    public async Task Should_CapTextAtFiftyThousandCharacters()
    {
        _extractor.Result = new ExtractedJobPosting("T", "C", null, new string('x', 50_000 + 500), JobPostingSource.Readable);

        var preview = await PreviewAsync();

        JobPostingImportService.MaxTextCharacters.Should().Be(50_000);
        preview.Text.Length.Should().Be(50_000);
    }

    [Fact]
    public async Task Should_KeepText_When_ExactlyAtCap()
    {
        _extractor.Result = new ExtractedJobPosting("T", "C", null, new string('x', 50_000), JobPostingSource.Readable);

        (await PreviewAsync()).Text.Length.Should().Be(50_000);
    }

    [Fact]
    public async Task Should_NotSplitSurrogatePair_When_CappingText()
    {
        _extractor.Result = new ExtractedJobPosting("T", "C", null, new string('x', 49_999) + "\U0001F600", JobPostingSource.Readable);

        var preview = await PreviewAsync();

        preview.Text.Length.Should().Be(49_999);
    }

    [Fact]
    public void Should_ExposeFetchLimits()
    {
        JobPostingImportService.FetchTimeout.Should().Be(TimeSpan.FromSeconds(15));
        JobPostingImportService.MaxBodyBytes.Should().Be(3 * 1024 * 1024);
        JobPostingImportService.MaxRedirects.Should().Be(5);
    }

    private sealed class StubFetcher : IWebPageFetcher
    {
        public int Calls { get; private set; }

        public Uri? Requested { get; private set; }

        public Exception? Failure { get; set; }

        public FetchedPage? Page { get; set; }

        public Task<FetchedPage> FetchAsync(Uri url, CancellationToken cancellationToken)
        {
            Calls++;
            Requested = url;
            return Failure is null
                ? Task.FromResult(Page ?? new FetchedPage(Final, "<html></html>"))
                : Task.FromException<FetchedPage>(Failure);
        }
    }

    private sealed class StubExtractor : IJobPostingExtractor
    {
        public ExtractedJobPosting? Result { get; set; }

        public ExtractedJobPosting? Extract(string html, Uri pageUri) => Result;
    }
}
