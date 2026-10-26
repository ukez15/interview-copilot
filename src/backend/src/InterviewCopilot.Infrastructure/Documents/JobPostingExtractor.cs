using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using InterviewCopilot.Application.Documents;
using SmartReader;

namespace InterviewCopilot.Infrastructure.Documents;

/// <summary>
/// Finds the job description in a page: schema.org JobPosting JSON-LD first, then readable main content
/// (SmartReader). Works on the HTML string only; nothing here touches the network or logs page content.
/// </summary>
internal sealed partial class JobPostingExtractor : IJobPostingExtractor
{
    private static readonly HashSet<string> SkippedElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "template", "head", "svg", "iframe",
    };

    // One blank line between these; a plain line break around the rest of the block elements.
    private static readonly HashSet<string> ParagraphElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "h1", "h2", "h3", "h4", "h5", "h6", "ul", "ol", "table", "blockquote", "pre",
    };

    private static readonly HashSet<string> LineElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "div", "section", "article", "header", "footer", "main", "aside", "nav", "tr", "dl", "dt", "dd", "figure", "form", "address",
    };

    public ExtractedJobPosting? Extract(string html, Uri pageUri)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(pageUri);

        using var document = new HtmlParser().ParseDocument(html);
        var pageTitle = NullIfEmpty(CollapseInline(document.Title));

        return FromJsonLd(document, pageTitle) ?? FromReadableContent(html, pageUri, pageTitle);
    }

    private static ExtractedJobPosting? FromJsonLd(IHtmlDocument document, string? pageTitle)
    {
        foreach (var script in document.QuerySelectorAll("script[type=\"application/ld+json\" i]"))
        {
            JsonDocument json;
            try
            {
                json = JsonDocument.Parse(script.TextContent);
            }
            catch (JsonException)
            {
                continue;
            }

            using (json)
            {
                foreach (var node in Flatten(json.RootElement).Where(IsJobPosting))
                {
                    var description = ToPlainText(ReadString(node, "description"));
                    if (description.Length == 0)
                    {
                        continue;
                    }

                    var title = NullIfEmpty(CollapseInline(ReadString(node, "title") ?? ReadString(node, "name")));
                    var company = NullIfEmpty(CollapseInline(ReadOrganization(node)));
                    var text = new StringBuilder();
                    if (title is not null)
                    {
                        text.Append(title).Append('\n');
                    }

                    if (company is not null)
                    {
                        text.Append(company).Append('\n');
                    }

                    if (text.Length > 0)
                    {
                        text.Append('\n');
                    }

                    text.Append(description);
                    return new ExtractedJobPosting(title, company, pageTitle, text.ToString(), JobPostingSource.JsonLd);
                }
            }
        }

        return null;
    }

    private static ExtractedJobPosting? FromReadableContent(string html, Uri pageUri, string? pageTitle)
    {
        Article article;
        try
        {
            // The (uri, text) constructor parses the given HTML only; the (uri) forms would download the page.
            using var reader = new Reader(pageUri.ToString(), html);
            article = reader.GetArticle();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NullReferenceException or FormatException)
        {
            return null;
        }

        if (!article.IsReadable || string.IsNullOrWhiteSpace(article.TextContent))
        {
            return null;
        }

        var text = !string.IsNullOrWhiteSpace(article.Content) ? ToPlainText(article.Content) : string.Empty;
        if (text.Length == 0)
        {
            text = Tidy(article.TextContent);
        }

        return text.Length == 0
            ? null
            : new ExtractedJobPosting(null, null, pageTitle ?? NullIfEmpty(CollapseInline(article.Title)), text, JobPostingSource.Readable);
    }

    /// <summary>Yields every object in a JSON-LD payload: a single object, an array, and the members of @graph.</summary>
    private static IEnumerable<JsonElement> Flatten(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray().SelectMany(Flatten))
                {
                    yield return item;
                }

                break;
            case JsonValueKind.Object:
                yield return element;
                if (element.TryGetProperty("@graph", out var graph))
                {
                    foreach (var item in Flatten(graph))
                    {
                        yield return item;
                    }
                }

                break;
            default:
                break;
        }
    }

    private static bool IsJobPosting(JsonElement node)
    {
        if (!node.TryGetProperty("@type", out var type))
        {
            return false;
        }

        return type.ValueKind switch
        {
            JsonValueKind.String => IsJobPostingType(type.GetString()),
            JsonValueKind.Array => type.EnumerateArray().Any(t => t.ValueKind == JsonValueKind.String && IsJobPostingType(t.GetString())),
            _ => false,
        };
    }

    private static bool IsJobPostingType(string? type) =>
        type is not null
        && (type.Equals("JobPosting", StringComparison.OrdinalIgnoreCase)
            || type.EndsWith("/JobPosting", StringComparison.OrdinalIgnoreCase)
            || type.EndsWith(":JobPosting", StringComparison.OrdinalIgnoreCase));

    private static string? ReadString(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>hiringOrganization is an Organization object, its name as a string, or a list of either.</summary>
    private static string? ReadOrganization(JsonElement node)
    {
        if (!node.TryGetProperty("hiringOrganization", out var organization))
        {
            return null;
        }

        if (organization.ValueKind == JsonValueKind.Array)
        {
            organization = organization.EnumerateArray().FirstOrDefault();
        }

        return organization.ValueKind switch
        {
            JsonValueKind.String => organization.GetString(),
            JsonValueKind.Object => ReadString(organization, "name"),
            _ => null,
        };
    }

    /// <summary>
    /// HTML (or HTML that was entity-escaped once or twice) to plain text: block elements become line breaks,
    /// list items become "- " lines, whitespace is collapsed, at most one blank line between paragraphs.
    /// </summary>
    internal static string ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        // Some sites escape the markup (&lt;p&gt;) inside the JSON string; unescape up to twice.
        for (var i = 0; i < 2 && !html.Contains('<', StringComparison.Ordinal) && EscapedTag().IsMatch(html); i++)
        {
            html = WebUtility.HtmlDecode(html);
        }

        if (!html.Contains('<', StringComparison.Ordinal))
        {
            // Plain text (line breaks are meant): only unescape entities.
            return Tidy(WebUtility.HtmlDecode(html));
        }

        using var document = new HtmlParser().ParseDocument(html);
        var builder = new StringBuilder();
        if (document.Body is { } body)
        {
            Walk(body, builder);
        }

        return Tidy(builder.ToString());
    }

    private static void Walk(INode node, StringBuilder builder)
    {
        foreach (var child in node.ChildNodes)
        {
            switch (child)
            {
                case IText text:
                    builder.Append(Whitespace().Replace(text.Data, " "));
                    break;
                case IElement element when SkippedElements.Contains(element.LocalName):
                    break;
                case IElement { LocalName: "br" }:
                    builder.Append('\n');
                    break;
                case IElement { LocalName: "li" } item:
                    AppendListItem(item, builder);
                    break;
                case IElement { LocalName: "td" or "th" } cell:
                    builder.Append(' ');
                    Walk(cell, builder);
                    builder.Append(' ');
                    break;
                case IElement element:
                    var gap = GapAround(element.LocalName);
                    EnsureBreak(builder, gap);
                    Walk(element, builder);
                    EnsureBreak(builder, gap);
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>Line breaks to keep around a block element: 2 = blank line, 1 = new line, 0 = inline.</summary>
    private static int GapAround(string elementName)
    {
        if (ParagraphElements.Contains(elementName))
        {
            return 2;
        }

        return LineElements.Contains(elementName) ? 1 : 0;
    }

    /// <summary>Makes the text end with at least <paramref name="breaks"/> line breaks, without stacking them.</summary>
    private static void EnsureBreak(StringBuilder builder, int breaks)
    {
        if (breaks == 0)
        {
            return;
        }

        while (builder.Length > 0 && builder[^1] == ' ')
        {
            builder.Length--;
        }

        if (builder.Length == 0)
        {
            return;
        }

        var existing = 0;
        for (var i = builder.Length - 1; i >= 0 && builder[i] == '\n'; i--)
        {
            existing++;
        }

        builder.Append('\n', Math.Max(0, breaks - existing));
    }

    /// <summary>One "- " line per item; an empty item is dropped, and paragraphs inside an item stay together.</summary>
    private static void AppendListItem(IElement item, StringBuilder builder)
    {
        var inner = new StringBuilder();
        Walk(item, inner);
        var content = Tidy(inner.ToString()).Replace("\n\n", "\n", StringComparison.Ordinal);
        if (content.Length > 0)
        {
            EnsureBreak(builder, 1);
            builder.Append("- ").Append(content);
        }
    }

    /// <summary>Collapses whitespace inside each line, keeps at most one blank line, trims.</summary>
    private static string Tidy(string text)
    {
        var lines = new List<string>();
        var blank = false;
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var line = CollapseInline(raw);
            if (line.Length == 0)
            {
                blank = lines.Count > 0;
                continue;
            }

            if (blank)
            {
                lines.Add(string.Empty);
                blank = false;
            }

            lines.Add(line);
        }

        return string.Join('\n', lines);
    }

    private static string CollapseInline(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : Whitespace().Replace(value, " ").Trim();

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    [GeneratedRegex(@"\s+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Whitespace();

    [GeneratedRegex("&(amp;)*(lt|gt);", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EscapedTag();
}
