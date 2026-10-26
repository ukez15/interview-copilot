using System.Net;
using System.Text;
using InterviewCopilot.Benchmark;

namespace InterviewCopilot.UnitTests.Benchmark;

/// <summary>Manual clock: only moves when a test (or a fake handler) advances it.</summary>
internal sealed class FakeClock : IClock
{
    private long _ticks;

    public void Advance(TimeSpan by) => _ticks += by.Ticks;

    public long GetTimestamp() => _ticks;

    public TimeSpan GetElapsed(long startTimestamp) => TimeSpan.FromTicks(_ticks - startTimestamp);
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>Routes each request to a delegate so tests can script the backend.</summary>
internal sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public List<(HttpMethod Method, string Path, string Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.Method, request.RequestUri!.AbsolutePath, body));
        return await respond(request, cancellationToken);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>An SSE response whose body is the given chunks, with an optional hook before each chunk.</summary>
    public static HttpResponseMessage Sse(IEnumerable<string> chunks, Action<int>? beforeChunk = null) =>
        new(HttpStatusCode.OK)
        {
            Content = new StreamContent(new HookedStream(chunks.Select(c => Encoding.UTF8.GetBytes(c)).ToArray(), beforeChunk)),
        };
}

/// <summary>Serves one chunk per read; the hook runs before chunk i is returned (used to advance a fake clock).</summary>
internal sealed class HookedStream(byte[][] chunks, Action<int>? beforeChunk) : Stream
{
    private int _index;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_index >= chunks.Length)
        {
            return ValueTask.FromResult(0);
        }

        beforeChunk?.Invoke(_index);
        var chunk = chunks[_index++];
        chunk.CopyTo(buffer);
        return ValueTask.FromResult(chunk.Length);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
