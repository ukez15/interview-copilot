using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FluentAssertions;
using InterviewCopilot.Domain.Exceptions;
using InterviewCopilot.Infrastructure.Documents;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace InterviewCopilot.UnitTests.Infrastructure;

public class DocumentExtractorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("resume.pdf", true)]
    [InlineData("RESUME.PDF", true)]
    [InlineData("resume.docx", false)]
    [InlineData("resume.txt", false)]
    [InlineData("pdf", false)]
    public void Should_AcceptOnlyPdfExtension_When_PdfExtractor(string fileName, bool expected)
    {
        new PdfTextExtractor().CanExtract(fileName).Should().Be(expected);
    }

    [Theory]
    [InlineData("resume.docx", true)]
    [InlineData("Resume.DocX", true)]
    [InlineData("resume.doc", false)]
    [InlineData("resume.pdf", false)]
    public void Should_AcceptOnlyDocxExtension_When_DocxExtractor(string fileName, bool expected)
    {
        new DocxTextExtractor().CanExtract(fileName).Should().Be(expected);
    }

    [Fact]
    public async Task Should_ExtractPdfText_When_SinglePage()
    {
        await using var stream = new MemoryStream(BuildPdf("Led a team of five"));

        var text = await new PdfTextExtractor().ExtractAsync("cv.pdf", stream, Ct);

        text.Should().Contain("Led a team of five");
    }

    [Fact]
    public async Task Should_JoinPdfPagesWithBlankLine_When_MultiplePages()
    {
        await using var stream = new MemoryStream(BuildPdf("First page", "Second page"));

        var text = await new PdfTextExtractor().ExtractAsync("cv.pdf", stream, Ct);

        text.Should().Be("First page\n\nSecond page", "pages are joined by a blank line");
    }

    [Fact]
    public async Task Should_Fail_When_PdfHasNoTextLayer()
    {
        await using var stream = new MemoryStream(BuildPdf(string.Empty));

        var act = () => new PdfTextExtractor().ExtractAsync("scan.pdf", stream, Ct);

        await act.Should().ThrowAsync<DomainException>()
            .WithMessage("No readable text was found in 'scan.pdf' (scanned PDFs aren't supported yet).");
    }

    [Fact]
    public async Task Should_Fail_When_PdfIsCorrupt()
    {
        await using var stream = new MemoryStream([1, 2, 3, 4, 5]);

        var act = () => new PdfTextExtractor().ExtractAsync("bad.pdf", stream, Ct);

        await act.Should().ThrowAsync<DomainException>().WithMessage("Couldn't read 'bad.pdf'.");
    }

    [Fact]
    public async Task Should_ExtractDocxParagraphsInOrder()
    {
        await using var stream = new MemoryStream(BuildDocx("Summary", "Led a team of five", "Shipped v2"));

        var text = await new DocxTextExtractor().ExtractAsync("cv.docx", stream, Ct);

        text.Should().Be(string.Join('\n', "Summary", "Led a team of five", "Shipped v2"));
    }

    [Fact]
    public async Task Should_Fail_When_DocxIsCorrupt()
    {
        await using var stream = new MemoryStream([1, 2, 3, 4, 5]);

        var act = () => new DocxTextExtractor().ExtractAsync("bad.docx", stream, Ct);

        await act.Should().ThrowAsync<DomainException>().WithMessage("Couldn't read 'bad.docx'.");
    }

    [Fact]
    public async Task Should_ThrowArgumentNull_When_StreamIsNull()
    {
        var pdf = () => new PdfTextExtractor().ExtractAsync("a.pdf", null!, Ct);
        var docx = () => new DocxTextExtractor().ExtractAsync("a.docx", null!, Ct);

        await pdf.Should().ThrowAsync<ArgumentNullException>();
        await docx.Should().ThrowAsync<ArgumentNullException>();
    }

    private static byte[] BuildPdf(params string[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var pageText in pages)
        {
            var page = builder.AddPage(595, 842);
            if (pageText.Length > 0)
            {
                page.AddText(pageText, 12, new PdfPoint(25, 700), font);
            }
        }

        return builder.Build();
    }

    private static byte[] BuildDocx(params string[] paragraphs)
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document();
            var body = main.Document.AppendChild(new Body());
            foreach (var text in paragraphs)
            {
                var run = new Paragraph().AppendChild(new Run());
                run.AppendChild(new Text(text));
                body.AppendChild(run.Parent!);
            }

            main.Document.Save();
        }

        return stream.ToArray();
    }
}
