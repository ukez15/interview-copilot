using System.Text;
using InterviewCopilot.Application.Sessions;
using InterviewCopilot.Domain;
using InterviewCopilot.Domain.Exceptions;

namespace InterviewCopilot.Application.Documents;

/// <summary>Where the text of a job posting preview came from.</summary>
public enum JobPostingSource
{
    /// <summary>schema.org JobPosting JSON-LD embedded in the page.</summary>
    JsonLd,

    /// <summary>Readable main content of the page (no structured data found).</summary>
    Readable,
}

/// <summary>What the user sees before saving: an editable name and text. Nothing is stored.</summary>
public sealed record JobPostingPreviewDto(string SuggestedName, string Text, JobPostingSource Source);

/// <summary>A downloaded web page: the address it ended up at (after redirects) and its decoded HTML.</summary>
public sealed record FetchedPage(Uri FinalUri, string Html);

/// <summary>Downloads a web page the user pointed us at. Implementations must be SSRF-safe.</summary>
public interface IWebPageFetcher
{
    /// <summary>Downloads the page at <paramref name="url"/>, following redirects safely.</summary>
    /// <exception cref="DomainException">Any failure, with a message that is safe to show the user.</exception>
    Task<FetchedPage> FetchAsync(Uri url, CancellationToken cancellationToken);
}

/// <summary>A job description pulled out of a page, before naming and capping.</summary>
public sealed record ExtractedJobPosting(string? Title, string? Company, string? PageTitle, string Text, JobPostingSource Source);

/// <summary>Finds the job description in a page's HTML.</summary>
public interface IJobPostingExtractor
{
    /// <summary>Returns null when the page holds nothing usable (login wall, empty script shell).</summary>
    ExtractedJobPosting? Extract(string html, Uri pageUri);
}

/// <summary>
/// Fetches a job-posting link and returns a preview of the job description. Saving goes through the existing
/// document upload, so nothing is stored here.
/// </summary>
public sealed class JobPostingImportService(SessionService sessions, IWebPageFetcher fetcher, IJobPostingExtractor extractor)
{
    /// <summary>Total time allowed for the fetch, redirects included.</summary>
    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(15);

    public const int MaxBodyBytes = 3 * 1024 * 1024;

    public const int MaxRedirects = 5;

    public const int MaxTextCharacters = 50_000;

    public const string InvalidUrlMessage = "Enter a link that starts with http:// or https://.";

    public const string NothingFoundMessage = "We couldn't read a job description from that page. Paste the text instead.";

    private const string FileExtension = ".txt";
    private const string FallbackStem = "Job description";

    public async Task<JobPostingPreviewDto> PreviewAsync(Guid ownerId, Guid sessionId, string url, CancellationToken cancellationToken)
    {
        var session = await sessions.LoadOwnedAsync(ownerId, sessionId, cancellationToken);
        if (session.Documents.Count >= SessionLimits.MaxDocumentsPerSession)
        {
            throw new DomainException(
                $"A session can have at most {SessionLimits.MaxDocumentsPerSession} documents. Remove one first.");
        }

        return await PreviewAsync(url, cancellationToken);
    }

    /// <summary>
    /// Same preview without a session, for a link pasted into the create-session form before the session exists.
    /// </summary>
    public async Task<JobPostingPreviewDto> PreviewAsync(string url, CancellationToken cancellationToken)
    {
        var uri = ParseUrl(url);
        var page = await fetcher.FetchAsync(uri, cancellationToken);
        var extracted = extractor.Extract(page.Html, page.FinalUri);
        if (extracted is null || string.IsNullOrWhiteSpace(extracted.Text))
        {
            throw new DomainException(NothingFoundMessage);
        }

        return new JobPostingPreviewDto(
            SuggestName(extracted, page.FinalUri.Host),
            Cap(extracted.Text),
            extracted.Source);
    }

    private static Uri ParseUrl(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.UserInfo.Length > 0
            || uri.Host.Length == 0)
        {
            throw new DomainException(InvalidUrlMessage);
        }

        return uri;
    }

    private static string SuggestName(ExtractedJobPosting posting, string host)
    {
        var title = Clean(posting.Title);
        var company = Clean(posting.Company);
        var combined = title.Length > 0 && company.Length > 0 ? $"{title} - {company}" : string.Empty;

        var stem = new[] { combined, title, Clean(posting.PageTitle), Clean(host) }
            .FirstOrDefault(candidate => candidate.Length > 0)
            ?? FallbackStem;

        var maxStem = SessionLimits.MaxFileNameLength - FileExtension.Length;
        if (stem.Length > maxStem)
        {
            stem = stem[..maxStem];
            if (char.IsHighSurrogate(stem[^1]))
            {
                stem = stem[..^1];
            }

            stem = stem.TrimEnd(' ', '.');
        }

        return stem.Length == 0 ? FallbackStem + FileExtension : stem + FileExtension;
    }

    /// <summary>Drops characters that can't be in a file name, collapses whitespace, trims.</summary>
    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (char.IsControl(c) || Array.IndexOf(invalid, c) >= 0 || "<>:\"/\\|?*".Contains(c, StringComparison.Ordinal))
            {
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        return builder.ToString().TrimEnd('.');
    }

    private static string Cap(string text)
    {
        if (text.Length <= MaxTextCharacters)
        {
            return text;
        }

        var end = MaxTextCharacters;
        if (char.IsHighSurrogate(text[end - 1]))
        {
            end--;
        }

        return text[..end].TrimEnd();
    }
}
