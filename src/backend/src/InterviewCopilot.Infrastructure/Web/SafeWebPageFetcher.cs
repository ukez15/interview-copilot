using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using InterviewCopilot.Application.Documents;
using InterviewCopilot.Domain.Exceptions;

namespace InterviewCopilot.Infrastructure.Web;

/// <summary>Thrown from the connect callback when a host resolves to (or is) a blocked address.</summary>
public sealed class BlockedAddressException() : Exception("The address is blocked.");

/// <summary>
/// Fetches a user-supplied web page without letting it reach local or private addresses. Owns its HttpClient
/// (no IHttpClientFactory, whose logging writes request URLs) and logs nothing: URLs and page contents are
/// personal data. Redirects are followed by hand so every hop is re-validated, and the socket is opened to
/// an address that was checked at connect time, so a DNS answer can't change between check and use.
/// </summary>
internal sealed partial class SafeWebPageFetcher : IWebPageFetcher, IDisposable
{
    /// <summary>Request option read by the OpenTelemetry HTTP filter in ServiceDefaults. Keep both names identical.</summary>
    internal const string SkipTracingOptionName = "InterviewCopilot.SkipTracing";

    internal static readonly HttpRequestOptionsKey<bool> SkipTracingKey = new(SkipTracingOptionName);

    internal static readonly string UserAgent = BuildUserAgent();

    private static readonly TimeSpan PooledConnectionLifetime = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    static SafeWebPageFetcher() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public SafeWebPageFetcher()
        : this(CreateHandler(Dns.GetHostAddressesAsync), JobPostingImportService.FetchTimeout)
    {
    }

    /// <summary>For tests: a fake handler (no sockets) and a short timeout.</summary>
    internal SafeWebPageFetcher(HttpMessageHandler handler, TimeSpan? timeout = null)
    {
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _timeout = timeout ?? JobPostingImportService.FetchTimeout;
    }

    internal delegate Task<IPAddress[]> Resolver(string host, CancellationToken cancellationToken);

    public void Dispose() => _http.Dispose();

    public async Task<FetchedPage> FetchAsync(Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        EnsureAllowed(url);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(_timeout);
        try
        {
            return await FetchWithRedirectsAsync(url, linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DomainException("That page took too long to respond. Paste the text instead.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException
            or System.Security.Authentication.AuthenticationException or BlockedAddressException)
        {
            throw IsBlocked(ex)
                ? new DomainException("That link points to a local or private address, which isn't allowed.")
                : new DomainException("That page couldn't be reached. Paste the text instead.");
        }
    }

    /// <summary>
    /// Resolves <paramref name="host"/> and returns the addresses it may connect to. Rejects the whole host when
    /// any answer is blocked, or when there are none.
    /// </summary>
    internal static async Task<IReadOnlyList<IPAddress>> ResolveAllowedAsync(
        string host, Resolver resolver, CancellationToken cancellationToken)
    {
        var literal = host.Trim('[', ']');
        if (IPAddress.TryParse(literal, out var parsed))
        {
            return BlockedAddresses.IsBlocked(parsed) ? throw new BlockedAddressException() : [parsed];
        }

        var addresses = await resolver(host, cancellationToken);
        if (addresses.Length == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        if (addresses.Any(BlockedAddresses.IsBlocked))
        {
            throw new BlockedAddressException();
        }

        return addresses;
    }

    private static SocketsHttpHandler CreateHandler(Resolver resolver) => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = PooledConnectionLifetime,
        ConnectCallback = (context, cancellationToken) => ConnectAsync(context, resolver, cancellationToken),
    };

    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context, Resolver resolver, CancellationToken cancellationToken)
    {
        var addresses = await ResolveAllowedAsync(context.DnsEndPoint.Host, resolver, cancellationToken);
        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                last = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }

    private static void EnsureAllowed(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
        {
            throw new DomainException(JobPostingImportService.InvalidUrlMessage);
        }

        if (IPAddress.TryParse(url.Host.Trim('[', ']'), out var literal) && BlockedAddresses.IsBlocked(literal))
        {
            throw new DomainException("That link points to a local or private address, which isn't allowed.");
        }
    }

    private static bool IsBlocked(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is BlockedAddressException)
            {
                return true;
            }
        }

        return false;
    }

    private async Task<FetchedPage> FetchWithRedirectsAsync(Uri start, CancellationToken cancellationToken)
    {
        var current = start;
        var redirects = 0;
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml");
            request.Options.Set(SkipTracingKey, true);

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var status = (int)response.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308)
            {
                if (response.Headers.Location is not { } location)
                {
                    throw PageNotLoaded(status);
                }

                if (redirects >= JobPostingImportService.MaxRedirects)
                {
                    throw new DomainException("That link redirected too many times. Paste the text instead.");
                }

                redirects++;
                current = new Uri(current, location);
                EnsureAllowed(current);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw PageNotLoaded(status);
            }

            return new FetchedPage(current, await ReadHtmlAsync(response, cancellationToken));
        }
    }

    private static DomainException PageNotLoaded(int status) =>
        new($"That page couldn't be loaded (HTTP {status}). Paste the text instead.");

    private static async Task<string> ReadHtmlAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var contentType = response.Content.Headers.ContentType;
        var mediaType = contentType?.MediaType;
        if (!string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(mediaType, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException("That link isn't a web page. Paste the text instead.");
        }

        if (response.Content.Headers.ContentLength > JobPostingImportService.MaxBodyBytes)
        {
            throw TooLarge();
        }

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > JobPostingImportService.MaxBodyBytes)
            {
                throw TooLarge();
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        buffer.Position = 0;
        using var reader = new StreamReader(buffer, PickEncoding(contentType, buffer.GetBuffer().AsSpan(0, (int)buffer.Length)), detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static DomainException TooLarge() => new("That page is too large to read. Paste the text instead.");

    /// <summary>Header charset first, then a &lt;meta charset&gt; near the top, else UTF-8.</summary>
    private static Encoding PickEncoding(MediaTypeHeaderValue? contentType, ReadOnlySpan<byte> bytes)
    {
        return FromName(contentType?.CharSet)
            ?? FromName(MetaCharset().Match(Encoding.Latin1.GetString(bytes[..Math.Min(bytes.Length, 2048)])) is { Success: true } m
                ? m.Groups[1].Value
                : null)
            ?? Encoding.UTF8;

        static Encoding? FromName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            try
            {
                return Encoding.GetEncoding(name.Trim().Trim('"', '\''));
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }

    private static string BuildUserAgent()
    {
        var version = typeof(SafeWebPageFetcher).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            version = version[..plus];
        }

        return $"InterviewCopilot/{version} (+https://github.com/ukez15/interview-copilot)";
    }

    [GeneratedRegex("""charset\s*=\s*["']?([A-Za-z0-9_\-:.]+)""", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MetaCharset();
}
