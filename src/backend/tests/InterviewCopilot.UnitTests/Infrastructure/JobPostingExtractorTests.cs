using System.Text.Json;
using FluentAssertions;
using InterviewCopilot.Application.Documents;
using InterviewCopilot.Infrastructure.Documents;

namespace InterviewCopilot.UnitTests.Infrastructure;

/// <summary>All HTML here is synthetic, written for these tests; none of it is copied from a real job site.</summary>
public class JobPostingExtractorTests
{
    private static readonly Uri PageUri = new("https://jobs.example.com/roles/42");
    private readonly JobPostingExtractor _extractor = new();

    private const string DescriptionHtml =
        "<p>We build <b>APIs</b>.</p><h2>Requirements</h2><ul><li>C#</li><li> SQL   Server </li></ul><p>Apply&nbsp;today<br>Now</p>";

    private const string ExpectedDescription = "We build APIs.\n\nRequirements\n\n- C#\n- SQL Server\n\nApply today\nNow";

    private static string Page(string ldJson, string title = "Careers - Acme", string body = "<p>Apply now</p>") =>
        $$"""
        <!doctype html><html><head><title>{{title}}</title>
        <script type="application/ld+json">{{ldJson}}</script></head><body>{{body}}</body></html>
        """;

    private static string Posting(string extra = "", string description = DescriptionHtml) =>
        $$"""{"@context":"https://schema.org","@type":"JobPosting","title":"Backend Engineer","hiringOrganization":{"@type":"Organization","name":"Acme"},"description":{{JsonSerializer.Serialize(description)}}{{extra}}}""";

    private ExtractedJobPosting? Extract(string html) => _extractor.Extract(html, PageUri);

    [Fact]
    public void Should_ReadSingleJobPostingObject()
    {
        var result = Extract(Page(Posting()));

        result.Should().NotBeNull();
        result.Source.Should().Be(JobPostingSource.JsonLd);
        result.Title.Should().Be("Backend Engineer");
        result.Company.Should().Be("Acme");
        result.PageTitle.Should().Be("Careers - Acme");
        result.Text.Should().Be($"Backend Engineer\nAcme\n\n{ExpectedDescription}");
    }

    [Fact]
    public void Should_ReadJobPostingFromArray()
    {
        var result = Extract(Page($$"""[{"@type":"WebSite","name":"Acme"},{{Posting()}}]"""));

        result!.Source.Should().Be(JobPostingSource.JsonLd);
        result.Title.Should().Be("Backend Engineer");
    }

    [Fact]
    public void Should_ReadJobPostingFromGraph()
    {
        var result = Extract(Page($$"""{"@context":"https://schema.org","@graph":[{"@type":"Organization","name":"Acme"},{{Posting()}}]}"""));

        result!.Source.Should().Be(JobPostingSource.JsonLd);
        result.Company.Should().Be("Acme");
    }

    [Theory]
    [InlineData("""["Thing","JobPosting"]""")]
    [InlineData("\"schema:JobPosting\"")]
    [InlineData("\"https://schema.org/JobPosting\"")]
    [InlineData("\"jobposting\"")]
    public void Should_MatchJobPostingType_InAllItsForms(string typeJson)
    {
        var json = Posting().Replace("\"@type\":\"JobPosting\"", $"\"@type\":{typeJson}", StringComparison.Ordinal);

        Extract(Page(json))!.Source.Should().Be(JobPostingSource.JsonLd);
    }

    [Theory]
    [InlineData("\"Organization\"")]
    [InlineData("42")]
    [InlineData("[\"Thing\"]")]
    public void Should_IgnoreOtherTypes(string typeJson)
    {
        var json = Posting().Replace("\"@type\":\"JobPosting\"", $"\"@type\":{typeJson}", StringComparison.Ordinal);

        Extract(Page(json)).Should().BeNull();
    }

    [Fact]
    public void Should_IgnoreNodeWithoutType()
    {
        Extract(Page("""{"title":"x","description":"<p>y</p>"}""")).Should().BeNull();
    }

    [Fact]
    public void Should_AcceptHiringOrganizationAsString()
    {
        var json = Posting().Replace("""{"@type":"Organization","name":"Acme"}""", "\"Acme Corp\"", StringComparison.Ordinal);

        var result = Extract(Page(json));

        result!.Company.Should().Be("Acme Corp");
        result.Text.Should().StartWith("Backend Engineer\nAcme Corp\n\n");
    }

    [Fact]
    public void Should_AcceptHiringOrganizationAsList()
    {
        var json = Posting().Replace("""{"@type":"Organization","name":"Acme"}""", """[{"name":"First Co"},{"name":"Second Co"}]""", StringComparison.Ordinal);

        Extract(Page(json))!.Company.Should().Be("First Co");
    }

    [Fact]
    public void Should_OmitCompanyLine_When_NoHiringOrganization()
    {
        var json = Posting().Replace(""","hiringOrganization":{"@type":"Organization","name":"Acme"}""", string.Empty, StringComparison.Ordinal);

        var result = Extract(Page(json));

        result!.Company.Should().BeNull();
        result.Text.Should().Be($"Backend Engineer\n\n{ExpectedDescription}");
    }

    [Fact]
    public void Should_FallBackToNameProperty_When_NoTitle()
    {
        var json = Posting().Replace("\"title\":\"Backend Engineer\"", "\"name\":\"Platform Engineer\"", StringComparison.Ordinal);

        Extract(Page(json))!.Title.Should().Be("Platform Engineer");
    }

    [Fact]
    public void Should_StartWithDescription_When_NoTitleOrCompany()
    {
        var json = """{"@type":"JobPosting","description":"<p>Just text</p>"}""";

        var result = Extract(Page(json));

        result!.Text.Should().Be("Just text");
        result.Title.Should().BeNull();
    }

    [Fact]
    public void Should_DecodeDescription_When_MarkupWasEntityEscapedOnce()
    {
        var escaped = "&lt;p&gt;We build &lt;b&gt;APIs&lt;/b&gt;.&lt;/p&gt;&lt;ul&gt;&lt;li&gt;C#&lt;/li&gt;&lt;/ul&gt;";

        var result = Extract(Page(Posting(description: escaped)));

        result!.Text.Should().EndWith("We build APIs.\n\n- C#");
        result.Text.Should().NotContain("&lt;").And.NotContain("<p>");
    }

    [Fact]
    public void Should_DecodeDescription_When_MarkupWasEntityEscapedTwice()
    {
        var escaped = "&amp;lt;p&amp;gt;Twice &amp;amp; done&amp;lt;/p&amp;gt;";

        var result = Extract(Page(Posting(description: escaped)));

        result!.Text.Should().EndWith("Twice & done");
        result.Text.Should().NotContain("&lt;").And.NotContain("&amp;");
    }

    [Fact]
    public void Should_KeepPlainDescription_When_ItHasNoMarkup()
    {
        var result = Extract(Page(Posting(description: "Line one.\n\n\n\nLine two   spaced.")));

        result!.Text.Should().EndWith("Line one.\n\nLine two spaced.");
    }

    [Fact]
    public void Should_SkipJobPostingWithoutDescription_And_UseNextOne()
    {
        var empty = Posting(description: "   ");
        var good = Posting().Replace("Backend Engineer", "Second Job", StringComparison.Ordinal);

        var result = Extract(Page($"[{empty},{good}]"));

        result!.Title.Should().Be("Second Job");
    }

    [Fact]
    public void Should_UseLaterScript_When_EarlierJsonLdIsInvalid()
    {
        var html = $$"""
            <html><head><title>T</title>
            <script type="application/ld+json">{ this is not json </script>
            <script type="application/ld+json">{{Posting()}}</script></head><body></body></html>
            """;

        Extract(html)!.Source.Should().Be(JobPostingSource.JsonLd);
    }

    [Fact]
    public void Should_MatchJsonLdScriptType_CaseInsensitively()
    {
        var html = $$"""<html><head><script type="Application/LD+JSON">{{Posting()}}</script></head><body></body></html>""";

        Extract(html)!.Source.Should().Be(JobPostingSource.JsonLd);
    }

    [Fact]
    public void Should_FallBackToReadableContent_When_JsonLdIsInvalid()
    {
        var html = Page("{ broken", title: "Staff Engineer at Beta", body: ArticleBody());

        var result = Extract(html);

        result.Should().NotBeNull();
        result.Source.Should().Be(JobPostingSource.Readable);
        result.PageTitle.Should().Be("Staff Engineer at Beta");
        result.Title.Should().BeNull();
        result.Text.Should().Contain("distributed systems").And.Contain("- Design reliable services");
    }

    [Fact]
    public void Should_FallBackToReadableContent_When_JsonLdIsNotAJobPosting()
    {
        var html = Page("""{"@type":"Organization","name":"Acme"}""", body: ArticleBody());

        Extract(html)!.Source.Should().Be(JobPostingSource.Readable);
    }

    [Fact]
    public void Should_ReadArticlePage_WithoutStructuredData()
    {
        var html = $"<!doctype html><html><head><title>Senior Developer | Beta Ltd</title></head><body>{Chrome()}{ArticleBody()}</body></html>";

        var result = Extract(html);

        result.Should().NotBeNull();
        result.Source.Should().Be(JobPostingSource.Readable);
        result.PageTitle.Should().Be("Senior Developer | Beta Ltd");
        result.Text.Should().Contain("distributed systems");
        result.Text.Should().NotContain("<").And.NotContain("Sign in to continue");
    }

    [Fact]
    public void Should_ReturnNull_ForLoginWall()
    {
        var html = """
            <html><head><title>Sign in</title></head>
            <body><div class="modal"><h1>Sign in to continue</h1>
            <form><input name="u"><input type="password" name="p"><button>Sign in</button></form></div></body></html>
            """;

        Extract(html).Should().BeNull();
    }

    [Fact]
    public void Should_ReturnNull_ForEmptyScriptShell()
    {
        var html = """
            <html><head><title>Loading</title><script src="/app.js"></script></head>
            <body><div id="root"></div><noscript>You need JavaScript to run this app.</noscript></body></html>
            """;

        Extract(html).Should().BeNull();
    }

    [Fact]
    public void Should_ReturnNull_ForEmptyDocument()
    {
        Extract(string.Empty).Should().BeNull();
    }

    [Fact]
    public void Should_ThrowOnNullArguments()
    {
        var html = () => _extractor.Extract(null!, PageUri);
        var uri = () => _extractor.Extract("<html/>", null!);

        html.Should().Throw<ArgumentNullException>();
        uri.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ToPlainText_Should_ConvertBlocksBulletsAndWhitespace()
    {
        var text = JobPostingExtractor.ToPlainText(
            "<div>Intro   text</div><div>Second<br/>line</div><p>Para</p><p>Para 2</p><ol><li>One</li><li></li><li><p>Two</p></li></ol>"
            + "<script>alert(1)</script><style>p{}</style><table><tr><td>A</td><td>B</td></tr></table>");

        text.Should().Be("Intro text\nSecond\nline\n\nPara\n\nPara 2\n\n- One\n- Two\n\nA B");
    }

    [Fact]
    public void ToPlainText_Should_ReturnEmpty_ForBlankInput()
    {
        JobPostingExtractor.ToPlainText(null).Should().BeEmpty();
        JobPostingExtractor.ToPlainText("  \n ").Should().BeEmpty();
        JobPostingExtractor.ToPlainText("<p> </p>").Should().BeEmpty();
    }

    private static string Chrome() =>
        "<nav><a href=\"/\">Home</a><a href=\"/jobs\">Jobs</a></nav><div class=\"banner\">Sign in to continue</div>";

    private static string ArticleBody() =>
        """
        <article>
        <h1>Senior Developer</h1>
        <p>Beta Ltd is hiring a senior developer to work on distributed systems that process orders for thousands of
        customers every day. You will join a small team, own services end to end, and talk to users regularly.</p>
        <p>The team values clear writing, careful testing, and steady delivery over heroics. You will review code,
        mentor colleagues, and help us decide what to build next, with plenty of time for design discussions.</p>
        <h2>What you will do</h2>
        <ul>
        <li>Design reliable services and the APIs between them</li>
        <li>Improve observability so problems are found before customers notice</li>
        <li>Pair with teammates and share what you learn in short write-ups</li>
        </ul>
        <p>We offer flexible hours, remote work within European time zones, a learning budget, and a generous
        holiday allowance. Please apply with a short note about a system you are proud of and why.</p>
        </article>
        """;
}
