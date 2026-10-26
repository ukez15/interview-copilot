using InterviewCopilot.Application.Documents;

namespace InterviewCopilot.Infrastructure.Documents;

/// <summary>
/// Plain-text documents (.txt, .md). PDF and DOCX have their own extractors.
/// </summary>
internal sealed class PlainTextExtractor : IDocumentTextExtractor
{
    private static readonly string[] Extensions = [".txt", ".md", ".markdown"];

    public bool CanExtract(string fileName) =>
        Extensions.Contains(Path.GetExtension(fileName ?? string.Empty), StringComparer.OrdinalIgnoreCase);

    public async Task<string> ExtractAsync(string fileName, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var reader = new StreamReader(content, leaveOpen: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }
}
