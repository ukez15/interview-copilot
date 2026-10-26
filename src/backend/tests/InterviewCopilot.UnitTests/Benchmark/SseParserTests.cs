using System.Text;
using FluentAssertions;
using InterviewCopilot.Benchmark;

namespace InterviewCopilot.UnitTests.Benchmark;

public sealed class SseParserTests
{
    [Fact]
    public async Task ReadEventsAsync_ShouldParseNamedEvents()
    {
        const string Payload = "event: meta\ndata: {\"version\":1}\n\nevent: delta\ndata: Hello\n\nevent: done\ndata: \n\n";

        var events = await ReadAllAsync(new ChunkedStream(Encoding.UTF8.GetBytes(Payload), Payload.Length));

        events.Select(e => e.Name).Should().Equal("meta", "delta", "done");
        events[0].Data.Should().Be("{\"version\":1}");
        events[1].Data.Should().Be("Hello");
        events[2].Data.Should().BeEmpty();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    public async Task ReadEventsAsync_ShouldGiveSameEvents_WhenChunkBoundariesFallAnywhere(int chunkSize)
    {
        const string Payload = "event: meta\r\ndata: x\r\n\r\nevent: delta\r\ndata: tok\r\n\r\nevent: done\r\ndata:\r\n\r\n";

        var events = await ReadAllAsync(new ChunkedStream(Encoding.UTF8.GetBytes(Payload), chunkSize));

        events.Select(e => e.Name).Should().Equal("meta", "delta", "done");
        events[1].Data.Should().Be("tok");
    }

    [Fact]
    public async Task ReadEventsAsync_ShouldSplitMultibyteCharacterAcrossChunks()
    {
        var payload = Encoding.UTF8.GetBytes("event: delta\ndata: café 你好\n\n");

        var events = await ReadAllAsync(new ChunkedStream(payload, 1));

        events.Should().ContainSingle().Which.Data.Should().Be("café 你好");
    }

    [Fact]
    public async Task ReadEventsAsync_ShouldJoinMultipleDataLinesAndIgnoreComments()
    {
        const string Payload = ": keep-alive\nevent: delta\ndata: line one\ndata: line two\n\n";

        var events = await ReadAllAsync(new ChunkedStream(Encoding.UTF8.GetBytes(Payload), 5));

        events.Should().ContainSingle().Which.Data.Should().Be("line one\nline two");
    }

    [Fact]
    public async Task ReadEventsAsync_ShouldDefaultNameToMessage_WhenNoEventField()
    {
        var events = await ReadAllAsync(new ChunkedStream(Encoding.UTF8.GetBytes("data: hi\n\n"), 4));

        events.Should().ContainSingle().Which.Name.Should().Be("message");
    }

    [Fact]
    public async Task ReadEventsAsync_ShouldDropIncompleteTrailingEvent()
    {
        const string Payload = "event: delta\ndata: one\n\nevent: delta\ndata: cut off";

        var events = await ReadAllAsync(new ChunkedStream(Encoding.UTF8.GetBytes(Payload), 6));

        events.Should().ContainSingle().Which.Data.Should().Be("one");
    }

    private static async Task<List<SseEvent>> ReadAllAsync(Stream stream)
    {
        var events = new List<SseEvent>();
        await foreach (var item in SseParser.ReadEventsAsync(stream, CancellationToken.None))
        {
            events.Add(item);
        }

        return events;
    }
}

/// <summary>Hands out at most <c>chunkSize</c> bytes per read, to exercise chunk boundaries.</summary>
internal sealed class ChunkedStream(byte[] data, int chunkSize) : Stream
{
    private int _position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = Math.Min(Math.Min(count, chunkSize), data.Length - _position);
        Array.Copy(data, _position, buffer, offset, n);
        _position += n;
        return n;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = Math.Min(Math.Min(buffer.Length, chunkSize), data.Length - _position);
        data.AsMemory(_position, n).CopyTo(buffer);
        _position += n;
        return ValueTask.FromResult(n);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
