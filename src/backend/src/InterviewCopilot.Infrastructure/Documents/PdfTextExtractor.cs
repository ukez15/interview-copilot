using InterviewCopilot.Application.Documents;
using InterviewCopilot.Domain.Exceptions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace InterviewCopilot.Infrastructure.Documents;

/// <summary>PDF documents (.pdf) with a text layer. Scanned PDFs (no text layer) are rejected with a clear message.</summary>
internal sealed class PdfTextExtractor : IDocumentTextExtractor
{
    private const string Extension = ".pdf";

    public bool CanExtract(string fileName) =>
        string.Equals(Path.GetExtension(fileName ?? string.Empty), Extension, StringComparison.OrdinalIgnoreCase);

    public async Task<string> ExtractAsync(string fileName, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;

        string text;
        try
        {
            using var document = PdfDocument.Open(buffer);
            // ContentOrderTextExtractor restores word spacing; page.Text can run words together in real-world PDFs.
            text = string.Join(
                "\n\n",
                document.GetPages().Select(page => ContentOrderTextExtractor.GetText(page).Trim()).Where(page => page.Length > 0));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new DomainException($"Couldn't read '{fileName}'.", ex);
        }

        return text.Length == 0
            ? throw new DomainException($"No readable text was found in '{fileName}' (scanned PDFs aren't supported yet).")
            : text;
    }
}
