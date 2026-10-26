using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using InterviewCopilot.Application.Documents;
using InterviewCopilot.Domain.Exceptions;
using InterviewCopilot.Infrastructure.Web;

namespace InterviewCopilot.UnitTests.Infrastructure;

public class SafeWebPageFetcherTests
{
    private const string BlockedMessage = "That link points to a local or private address, which isn't allowed.";
    private const string Html = "<html><head><title>Job</title></head><body>Hello</body></html>";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpResponseMessage HtmlResponse(string body = Html, string contentType = "text/html; charset=utf-8", HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8) };
        response.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
        return response;
    }

    private static HttpResponseMessage Redirect(string location, HttpStatusCode status = HttpStatusCode.Found)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private static SafeWebPageFetcher Fetcher(FakeHandler handler, TimeSpan? timeout = null) => new(handler, timeout);

    [Fact]
    public async Task Should_ReturnHtmlAndFinalUri_When_PageLoads()
    {
        var handler = new FakeHandler(_ => HtmlResponse());

        var page = await Fetcher(handler).FetchAsync(new Uri("https://jobs.example.com/a"), Ct);

        page.Html.Should().Be(Html);
        page.FinalUri.Should().Be(new Uri("https://jobs.example.com/a"));
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Should_SendUserAgentAcceptAndSkipTracingOption_AndNoCookiesOrAuth()
    {
        var handler = new FakeHandler(_ => HtmlResponse());

        await Fetcher(handler).FetchAsync(new Uri("https://jobs.example.com/a"), Ct);

        var request = handler.Requests.Single();
        request.UserAgent.Should().StartWith("InterviewCopilot/").And.EndWith(" (+https://github.com/ukez15/interview-copilot)");
        request.UserAgent.Replace("(+https", "(https", StringComparison.Ordinal).Should().NotContain("+", "build metadata is stripped");
        request.UserAgent.Should().Be(SafeWebPageFetcher.UserAgent);
        request.Accept.Should().Be("text/html,application/xhtml+xml");
        request.SkipTracing.Should().BeTrue();
        request.HeaderNames.Should().NotContain(["Cookie", "Authorization", "Accept-Language", "Referer"]);
        request.Method.Should().Be(HttpMethod.Get);
    }

    [Fact]
    public void Should_NameTheTracingOptionLikeServiceDefaultsDoes()
    {
        SafeWebPageFetcher.SkipTracingOptionName.Should().Be("InterviewCopilot.SkipTracing");
    }

    [Fact]
    public async Task Should_DecodeWithHeaderCharset_When_Present()
    {
        var bytes = Encoding.Latin1.GetBytes("<html><body>Caf\u00e9 r\u00e9sum\u00e9</body></html>");
        var handler = new FakeHandler(_ => BytesResponse(bytes, "text/html; charset=iso-8859-1"));

        var page = await Fetcher(handler).FetchAsync(new Uri("https://jobs.example.com/a"), Ct);

        page.Html.Should().Contain("Caf\u00e9 r\u00e9sum\u00e9");
    }

    [Fact]
    public async Task Should_DecodeWithMetaCharset_When_HeaderHasNone()
    {
        var bytes = Encoding.Latin1.GetBytes("<html><head><meta charset=\"windows-1252\"></head><body>Caf\u00e9</body></html>");
        var handler = new FakeHandler(_ => BytesResponse(bytes, "text/html"));

        var page = await Fetcher(handler).FetchAsync(new Uri("https://jobs.example.com/a"), Ct);

        page.Html.Should().Contain("Caf\u00e9");
    }

    [Fact]
    public async Task Should_FallBackToUtf8_When_CharsetIsUnknown()
    {
        var bytes = Encoding.UTF8.GetBytes("<html><body>Caf\u00e9</body></html>");
        var handler = new FakeHandler(_ => BytesResponse(bytes, "text/html; charset=not-a-charset"));

        var page = await Fetcher(handler).FetchAsync(new Uri("https://jobs.example.com/a"), Ct);

        page.Html.Should().Contain("Caf\u00e9");
    }

    [Fact]
    public async Task Should_StripUtf8Bom()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("<html>x</html>")).ToArray();
        var handler = new FakeHandler(_ => BytesResponse(bytes, "text/html"));

        var page = await Fetcher(handler).FetchAsync(new Uri("https://jobs.example.com/a"), Ct);

        page.Html.Should().Be("<html>x</html>");
    }

    [Fact]
    public async Task Should_AcceptXhtml()
    {
        var handler = new FakeHandler(_ => HtmlResponse(contentType: "application/xhtml+xml"));

        var page = await Fetcher(handler).FetchAsync(new Uri("https://jobs.example.com/a"), Ct);

        page.Html.Should().Be(Html);
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.SeeOther)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task Should_FollowRedirect_AndReportFinalUri(HttpStatusCode status)
    {
        var handler = new FakeHandler(r => r.Uri.AbsolutePath == "/a" ? Redirect("https://other.example.com/b", status) : HtmlResponse());

        var page = await Fetcher(handler).FetchAsync(new Uri("https://jobs.example.com/a"), Ct);

        page.FinalUri.Should().Be(new Uri("https://other.example.com/b"));
        handler.Requests.Select(r => r.Uri.Host).Should().Equal("jobs.example.com", "other.example.com");
    }

    [Fact]
    public async Task Should_ResolveRelativeRedirectAgainstCurrentUri()
    {
        var handler = new FakeHandler(r => r.Uri.AbsolutePath switch
        {
            "/jobs/a" => Redirect("../moved/b?x=1"),
            _ => HtmlResponse(),
        });

        var page = await Fetcher(handler).FetchAsync(new Uri("https://jobs.example.com/jobs/a"), Ct);

        page.FinalUri.Should().Be(new Uri("https://jobs.example.com/moved/b?x=1"));
    }

    [Theory]
    [InlineData("http://127.0.0.1/admin")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://[::1]/")]
    [InlineData("http://10.0.0.5/")]
    [InlineData("http://[::ffff:192.168.0.1]/")]
    public async Task Should_RejectRedirectToPrivateLiteral(string target)
    {
        var handler = new FakeHandler(r => Redirect(target));

        var act = () => Fetcher(handler).FetchAsync(new Uri("https://jobs.example.com/a"), Ct);

        (await act.Should().ThrowAsync<DomainException>()).WithMessage(BlockedMessage);
        handler.Requests.Should().ContainSingle("the private target must never be requested");
    }

    [Fact]
    public async Task Should_RejectRedirectToNonHttpScheme()
    {
        var handler = new FakeHandler(r => Redirect("file:///c:/secret.txt"));

        var act = () => Fetcher(handler).FetchAsync(new Uri("https://jobs.example.com/a"), Ct);

        (await act.Should().ThrowAsync<DomainException>()).WithMessage("Enter a link that starts with http:// or https://.");
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Should_Fail_When_TooManyRedirects()
    {
        var handler = new FakeHandler(r => Redirect($"/hop{r.Uri.AbsolutePath.Length}"));

        var act = () => Fetcher(handler).FetchAsync(new Uri("https://jobs.example.com/a"), Ct);

        (await act.Should().ThrowAsync<DomainException>()).WithMessage("That link redirected too many times. Paste the text instead.");
        handler.Requests.Should().HaveCount(JobPostingImportService.MaxRedirects + 1);
    }

    [Fact]
    public async Task Should_Succeed_When_ExactlyMaxRedirects()
    {
        var hops = 0;
        var handler = new FakeHandler(r => hops++ < JobPostingImportService.MaxRedirects ? Redirect($"/hop{hops}") : HtmlResponse());

        var page = await Fetcher(handler).FetchAsync(new Uri("https://jobs.example.com/a"), Ct);

        page.FinalUri.AbsolutePath.Should().Be($"/hop{JobPostingImportService.MaxRedirects}");
    }

    [Fact]
    public async Task Should_Fail_When_RedirectHasNoLocation()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Found));

        var act = () => Fetcher(handler).FetchAsync(new Uri("https://jobs.example.com/a"), Ct);

        (await act.Should().ThrowAsync<DomainException>()).WithMessage("That page couldn't be loaded (HTTP 302). Paste the text instead.");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, 404)]
    [InlineData(HttpStatusCode.Forbidden, 403)]
    [InlineData(HttpStatusCode.InternalServerError, 500)]
    [InlineData(HttpStatusCode.NotModified, 304)]
    public async Task Should_ReportStatus_When_NotSuccess(HttpStatusCode status, int code)
    {
        var handler = new FakeHandler(_ => HtmlResponse(status: status));

        var act = () => Fetcher(handler).FetchAsync(new Uri("https://jobs.example.com/a"), Ct);

        (await act.Should().ThrowAsync<DomainException>())
            .WithMessage($"That page couldn't be loaded (HTTP {code}). Paste the text instead.");
    }

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("image/png")]
    [InlineData("application/json")]
    [InlineData("text/plain")]
    public async Task Should_Reject_When_NotHtml(string contentType)
    {
        var handler = new FakeHandler(_ => HtmlResponse(contentType: contentType));

        var act = () => Fetcher(handler).FetchAsync(new Uri("https://jobs.example.com/a"), Ct);

        (await act.Should().ThrowAsync<DomainException>()).WithMessage("That link isn't a web page. Paste the text instead.");
    }

    [Fact]
    public async Task Should_Reject_When_ContentTypeMissing()
    {
        var handler = new FakeHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(Html)) };
            response.Content.Headers.ContentType = null;
            return response;
        });

        var act = () => Fetcher(handler).FetchAsync(new Uri("https://jobs.example.com/a"), Ct);

        (await act.Should().ThrowAsync<DomainException>()).WithMessage("That link isn't a web page. Paste the text instead.");
    }

    [Fact]
    public async Task Should_RejectImmediately_When_ContentLengthOverLimit()
    {
        var content = new NeverReadContent();
        var handler = new FakeHandler(_ =>
        {
            content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("text/html");
            content.Headers.ContentLength = JobPostingImportService.MaxBodyBytes + 1;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        var act = () => Fetcher(handler).FetchAsync(new Uri("https://jobs.example.com/a"), Ct);

        (await act.Should().ThrowAsync<DomainException>()).WithMessage("That page is too large to read. Paste the text instead.");
        content.Read.Should().BeFalse();
    }

    [Fact]
    public async Task Should_StopReading_When_StreamedBodyExceedsLimit()
    {
        var content = new EndlessContent();
        var handler = new FakeHandler(_ =>
        {
            content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("text/html");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        var act = () => Fetcher(handler).FetchAsync(new Uri("https://jobs.example.com/a"), Ct);

        (await act.Should().ThrowAsync<DomainException>()).WithMessage("That page is too large to read. Paste the text instead.");
        content.BytesServed.Should().BeLessThan(JobPostingImportService.MaxBodyBytes + 64 * 1024);
    }

    [Fact]
    public async Task Should_AcceptBody_When_ExactlyAtLimit()
    {
        var bytes = Enumerable.Repeat((byte)'a', JobPostingImportService.MaxBodyBytes).ToArray();
        var handler = new FakeHandler(_ => BytesResponse(bytes, "text/html"));

        var page = await Fetcher(handler).FetchAsync(new Uri("https://jobs.example.com/a"), Ct);

        page.Html.Length.Should().Be(JobPostingImportService.MaxBodyBytes);
    }

    [Fact]
    public async Task Should_TimeOut_When_ServerNeverAnswers()
    {
        var handler = new FakeHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return HtmlResponse();
        });

        var act = () => Fetcher(handler, TimeSpan.FromMilliseconds(150)).FetchAsync(new Uri("https://jobs.example.com/a"), Ct);

        (await act.Should().ThrowAsync<DomainException>()).WithMessage("That page took too long to respond. Paste the text instead.");
    }

    [Fact]
    public async Task Should_TimeOut_When_RedirectsEatTheWholeBudget()
    {
        var handler = new FakeHandler(async (r, ct) =>
        {
            await Task.Delay(60, ct);
            return Redirect($"/hop{r.Uri.AbsolutePath.Length}");
        });

        var act = () => Fetcher(handler, TimeSpan.FromMilliseconds(150)).FetchAsync(new Uri("https://jobs.example.com/a"), Ct);

        (await act.Should().ThrowAsync<DomainException>()).WithMessage("That page took too long to respond. Paste the text instead.");
    }

    [Fact]
    public async Task Should_PropagateCancellation_When_CallerCancels()
    {
        using var cts = new CancellationTokenSource();
        var handler = new FakeHandler(async (_, ct) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return HtmlResponse();
        });

        var act = () => Fetcher(handler).FetchAsync(new Uri("https://jobs.example.com/a"), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("file:///c:/x")]
    [InlineData("ftp://example.com/x")]
    public async Task Should_RejectNonHttpScheme_BeforeAnyRequest(string url)
    {
        var handler = new FakeHandler(_ => HtmlResponse());

        var act = () => Fetcher(handler).FetchAsync(new Uri(url), Ct);

        (await act.Should().ThrowAsync<DomainException>()).WithMessage("Enter a link that starts with http:// or https://.");
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("http://127.0.0.1:5480/api")]
    [InlineData("http://[::1]/")]
    [InlineData("http://10.1.2.3/")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://169.254.169.254/")]
    [InlineData("http://100.64.0.1/")]
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    [InlineData("http://2130706433/")]
    public async Task Should_RejectPrivateLiteral_BeforeAnyRequest(string url)
    {
        var handler = new FakeHandler(_ => HtmlResponse());

        var act = () => Fetcher(handler).FetchAsync(new Uri(url), Ct);

        (await act.Should().ThrowAsync<DomainException>()).WithMessage(BlockedMessage);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Should_ShowBlockedMessage_When_ConnectCallbackFailureIsWrapped()
    {
        var handler = new FakeHandler((_, _) => throw new HttpRequestException("connect failed", new BlockedAddressException()));

        var act = () => Fetcher(handler).FetchAsync(new Uri("https://rebind.example.com/"), Ct);

        (await act.Should().ThrowAsync<DomainException>()).WithMessage(BlockedMessage);
    }

    [Theory]
    [MemberData(nameof(NetworkFailures))]
    public async Task Should_ShowUnreachableMessage_When_NetworkFails(Exception failure)
    {
        var handler = new FakeHandler((_, _) => throw failure);

        var act = () => Fetcher(handler).FetchAsync(new Uri("https://jobs.example.com/"), Ct);

        (await act.Should().ThrowAsync<DomainException>()).WithMessage("That page couldn't be reached. Paste the text instead.");
    }

    public static TheoryData<Exception> NetworkFailures() => new()
    {
        new HttpRequestException("dns", new SocketException((int)SocketError.HostNotFound)),
        new HttpRequestException("refused", new SocketException((int)SocketError.ConnectionRefused)),
        new HttpRequestException("tls", new System.Security.Authentication.AuthenticationException("bad cert")),
        new IOException("reset"),
    };

    [Fact]
    public async Task Should_BlockLocalhost_ThroughTheRealHandler_WithoutNetwork()
    {
        // The real handler resolves "localhost" via the hosts file only; the connect callback must refuse it.
        using var fetcher = new SafeWebPageFetcher();

        var act = () => fetcher.FetchAsync(new Uri("http://localhost:9/"), Ct);

        (await act.Should().ThrowAsync<DomainException>()).WithMessage(BlockedMessage);
    }

    [Fact]
    public async Task ResolveAllowed_Should_ReturnAddresses_When_AllPublic()
    {
        IPAddress[] answer = [IPAddress.Parse("93.184.216.34"), IPAddress.Parse("2606:4700::1111")];

        var result = await SafeWebPageFetcher.ResolveAllowedAsync("jobs.example.com", (_, _) => Task.FromResult(answer), Ct);

        result.Should().Equal(answer);
    }

    [Fact]
    public async Task ResolveAllowed_Should_Reject_When_AnyAddressIsPrivate()
    {
        IPAddress[] answer = [IPAddress.Parse("93.184.216.34"), IPAddress.Parse("10.0.0.7")];

        var act = () => SafeWebPageFetcher.ResolveAllowedAsync("rebind.example.com", (_, _) => Task.FromResult(answer), Ct);

        await act.Should().ThrowAsync<BlockedAddressException>();
    }

    [Fact]
    public async Task ResolveAllowed_Should_Reject_When_OnlyPrivate()
    {
        IPAddress[] answer = [IPAddress.Loopback];

        var act = () => SafeWebPageFetcher.ResolveAllowedAsync("localtest.example.com", (_, _) => Task.FromResult(answer), Ct);

        await act.Should().ThrowAsync<BlockedAddressException>();
    }

    [Fact]
    public async Task ResolveAllowed_Should_Fail_When_NothingResolves()
    {
        var act = () => SafeWebPageFetcher.ResolveAllowedAsync("nowhere.example.com", (_, _) => Task.FromResult(Array.Empty<IPAddress>()), Ct);

        await act.Should().ThrowAsync<SocketException>();
    }

    [Fact]
    public async Task ResolveAllowed_Should_PropagateResolverFailure()
    {
        var act = () => SafeWebPageFetcher.ResolveAllowedAsync(
            "nowhere.example.com", (_, _) => throw new SocketException((int)SocketError.HostNotFound), Ct);

        await act.Should().ThrowAsync<SocketException>();
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("[::1]", true)]
    [InlineData("::1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("[2606:4700:4700::1111]", false)]
    public async Task ResolveAllowed_Should_CheckLiteralsWithoutResolving(string host, bool blocked)
    {
        var resolverCalled = false;
        Task<IPAddress[]> Resolve(string h, CancellationToken ct)
        {
            resolverCalled = true;
            return Task.FromResult(Array.Empty<IPAddress>());
        }

        var act = () => SafeWebPageFetcher.ResolveAllowedAsync(host, Resolve, Ct);

        if (blocked)
        {
            await act.Should().ThrowAsync<BlockedAddressException>();
        }
        else
        {
            (await act()).Should().ContainSingle();
        }

        resolverCalled.Should().BeFalse();
    }

    private static HttpResponseMessage BytesResponse(byte[] bytes, string contentType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
        return response;
    }

    private sealed record SeenRequest(
        Uri Uri, HttpMethod Method, string? UserAgent, string? Accept, bool SkipTracing, IReadOnlyList<string> HeaderNames);

    private sealed class FakeHandler(Func<SeenRequest, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public FakeHandler(Func<SeenRequest, HttpResponseMessage> respond)
            : this((request, _) => Task.FromResult(respond(request)))
        {
        }

        public List<SeenRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var seen = new SeenRequest(
                request.RequestUri!,
                request.Method,
                request.Headers.UserAgent.ToString(),
                request.Headers.Accept.ToString().Replace(", ", ",", StringComparison.Ordinal),
                request.Options.TryGetValue(SafeWebPageFetcher.SkipTracingKey, out var skip) && skip,
                [.. request.Headers.Select(h => h.Key)]);
            Requests.Add(seen);
            return respond(seen, cancellationToken);
        }
    }

    private sealed class NeverReadContent : HttpContent
    {
        public bool Read { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            Read = true;
            return Task.CompletedTask;
        }

        protected override Stream CreateContentReadStream(CancellationToken cancellationToken)
        {
            Read = true;
            return Stream.Null;
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            Read = true;
            return Task.FromResult(Stream.Null);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class EndlessContent : HttpContent
    {
        public long BytesServed { get; set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new EndlessStream(this));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        private sealed class EndlessStream(EndlessContent owner) : Stream
        {
            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count) => Fill(buffer.AsSpan(offset, count));

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
                ValueTask.FromResult(Fill(buffer.Span));

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            private int Fill(Span<byte> span)
            {
                span.Fill((byte)'a');
                owner.BytesServed += span.Length;
                return span.Length;
            }
        }
    }
}
