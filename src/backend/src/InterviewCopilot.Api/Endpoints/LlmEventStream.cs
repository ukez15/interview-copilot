using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using InterviewCopilot.Application.Providers;

namespace InterviewCopilot.Api.Endpoints;

/// <summary>Relays a model's output as server-sent events, ending with an "error" event if the model fails part-way.</summary>
internal static class LlmEventStream
{
    public const string Delta = "delta";
    public const string Done = "done";
    public const string Error = "error";

    /// <summary>Maps each item to an event, then sends <paramref name="last"/> (if any) once the items run out.</summary>
    /// <param name="items">The model's output; an <see cref="LlmUnavailableException"/> while reading it becomes "error".</param>
    /// <param name="toEvent">Turns one item into its event.</param>
    /// <param name="last">The closing event after a clean finish; null when the items carry their own.</param>
    /// <param name="cancellationToken">Ends the stream when the client goes away.</param>
    public static async IAsyncEnumerable<SseItem<string>> RelayAsync<T>(
        IAsyncEnumerable<T> items,
        Func<T, SseItem<string>> toEvent,
        SseItem<string>? last,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var enumerator = items.GetAsyncEnumerator(cancellationToken);
        string? error = null;
        try
        {
            while (true)
            {
                T item;
                try
                {
                    if (!await enumerator.MoveNextAsync())
                    {
                        break;
                    }

                    item = enumerator.Current;
                }
                catch (LlmUnavailableException ex)
                {
                    error = ex.Message;
                    break;
                }

                yield return toEvent(item);
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        if (error is not null)
        {
            yield return new SseItem<string>(error, Error);
        }
        else if (last is { } closing)
        {
            yield return closing;
        }
    }
}
