using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using InterviewCopilot.Application.Documents;
using InterviewCopilot.Domain.Exceptions;

namespace InterviewCopilot.Infrastructure.Documents;

/// <summary>Word documents (.docx): paragraph text in document order.</summary>
internal sealed class DocxTextExtractor : IDocumentTextExtractor
{
    private const string Extension = ".docx";

    public bool CanExtract(string fileName) =>
        string.Equals(Path.GetExtension(fileName ?? string.Empty), Extension, StringComparison.OrdinalIgnoreCase);

    public async Task<string> ExtractAsync(string fileName, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;

        try
        {
            using var document = WordprocessingDocument.Open(buffer, false);
            var body = document.MainDocumentPart?.Document?.Body
                ?? throw new InvalidDataException("Missing document body.");
            return string.Join('\n', body.Descendants<Paragraph>().Select(paragraph => paragraph.InnerText));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new DomainException($"Couldn't read '{fileName}'.", ex);
        }
    }
}
