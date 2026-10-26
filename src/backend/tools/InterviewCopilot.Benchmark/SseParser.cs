using System.Runtime.CompilerServices;
using System.Text;

namespace InterviewCopilot.Benchmark;

internal readonly record struct SseEvent(string Name, string Data);

/// <summary>
/// Minimal Server-Sent Events reader. Events are dispatched at the blank line, so a chunk boundary can fall
/// anywhere (mid-line, mid-event, mid-character). An event with a name but empty data (the backend's
/// <c>done</c>) is dispatched too; a trailing event the server never finished is dropped.
/// </summary>
internal static class SseParser
{
    private const string DefaultEventName = "message";

    public static async IAsyncEnumerable<SseEvent> ReadEventsAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        var name = DefaultEventName;
        var data = new StringBuilder();
        var dataLines = 0;
        var pending = false;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (pending)
                {
                    yield return new SseEvent(name, data.ToString());
                }

                name = DefaultEventName;
                data.Clear();
                dataLines = 0;
                pending = false;
                continue;
            }

            if (line[0] == ':')
            {
                continue; // comment / keep-alive
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? string.Empty : line[(colon + 1)..];
            if (value.StartsWith(' '))
            {
                value = value[1..];
            }

            if (field == "event")
            {
                name = value;
                pending = true;
            }
            else if (field == "data")
            {
                if (dataLines > 0)
                {
                    data.Append('\n');
                }

                data.Append(value);
                dataLines++;
                pending = true;
            }
        }
    }
}
