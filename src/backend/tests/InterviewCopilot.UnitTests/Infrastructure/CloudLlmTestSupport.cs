using System.Net;
using System.Text;

namespace InterviewCopilot.UnitTests.Infrastructure;

/// <summary>Fake HTTP backend for the BYOK adapters: records the request and replies with a scripted response.</summary>
internal sealed class CloudFakeHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public CloudFakeHandler(HttpStatusCode status, string body, string contentType = "text/event-stream")
        : this(_ => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, contentType) })
    {
    }

    public CloudFakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    public HttpRequestMessage? LastRequest { get; private set; }

    public string? LastBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        return _respond(request);
    }
}

/// <summary>A response body that yields one SSE chunk and then blocks until the reader is cancelled.</summary>
internal sealed class BlockingStream(string first) : Stream
{
    private readonly byte[] _first = Encoding.UTF8.GetBytes(first);
    private bool _sent;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public bool Cancelled { get; private set; }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!_sent)
        {
            _sent = true;
            _first.CopyTo(buffer);
            return _first.Length;
        }

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Cancelled = true;
            throw;
        }

        return 0;
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
