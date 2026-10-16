using System.Collections.Concurrent;
using System.Threading.Channels;

namespace InterviewCopilot.Application.Events;

/// <summary>One client's view of a session's events. Dispose to unsubscribe.</summary>
public sealed class SessionEventSubscription : IDisposable
{
    private readonly Action<SessionEventSubscription> _onDispose;
    private readonly Channel<SessionEvent> _channel;

    internal SessionEventSubscription(Channel<SessionEvent> channel, Action<SessionEventSubscription> onDispose)
    {
        _channel = channel;
        _onDispose = onDispose;
    }

    /// <summary>Events for this subscriber, oldest first. Completes after <see cref="Dispose"/>.</summary>
    public ChannelReader<SessionEvent> Reader => _channel.Reader;

    internal void Deliver(SessionEvent sessionEvent) => _channel.Writer.TryWrite(sessionEvent);

    /// <inheritdoc />
    public void Dispose()
    {
        _onDispose(this);
        _channel.Writer.TryComplete();
    }
}

/// <summary>
/// In-memory fan-out of session events (#23). Ids increase per session from 1; each subscriber has its own
/// bounded buffer that drops that subscriber's oldest events when full.
/// </summary>
public sealed class SessionEventHub : ISessionEventPublisher
{
    /// <summary>Events buffered per subscriber before the oldest are dropped.</summary>
    public const int DefaultBufferSize = 500;

    private readonly int _bufferSize;
    private readonly ConcurrentDictionary<Guid, SessionChannel> _sessions = new();

    /// <summary>Creates a hub.</summary>
    /// <param name="bufferSize">Events buffered per subscriber.</param>
    public SessionEventHub(int bufferSize = DefaultBufferSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, 1);
        _bufferSize = bufferSize;
    }

    /// <summary>Starts receiving the session's events published from now on.</summary>
    /// <param name="sessionId">The session to watch.</param>
    /// <returns>A subscription; dispose it when the client disconnects.</returns>
    public SessionEventSubscription Subscribe(Guid sessionId)
    {
        var channel = Channel.CreateBounded<SessionEvent>(new BoundedChannelOptions(_bufferSize)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        var state = _sessions.GetOrAdd(sessionId, static _ => new SessionChannel());
        var subscription = new SessionEventSubscription(channel, subscription => state.Remove(subscription));
        state.Add(subscription);
        return subscription;
    }

    /// <summary>Number of clients currently subscribed to the session.</summary>
    /// <param name="sessionId">The session to count.</param>
    /// <returns>The subscriber count.</returns>
    public int SubscriberCount(Guid sessionId) =>
        _sessions.TryGetValue(sessionId, out var state) ? state.Count : 0;

    /// <inheritdoc />
    public void Publish(Guid sessionId, string type, object data)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(data);
        _sessions.GetOrAdd(sessionId, static _ => new SessionChannel()).Publish(type, data);
    }

    private sealed class SessionChannel
    {
        private readonly object _gate = new();
        private readonly List<SessionEventSubscription> _subscribers = [];
        private long _lastId;

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _subscribers.Count;
                }
            }
        }

        public void Add(SessionEventSubscription subscription)
        {
            lock (_gate)
            {
                _subscribers.Add(subscription);
            }
        }

        public void Remove(SessionEventSubscription subscription)
        {
            lock (_gate)
            {
                _subscribers.Remove(subscription);
            }
        }

        public void Publish(string type, object data)
        {
            // Under the lock so ids reach every subscriber in order.
            lock (_gate)
            {
                var sessionEvent = new SessionEvent(++_lastId, type, data);
                foreach (var subscriber in _subscribers)
                {
                    subscriber.Deliver(sessionEvent);
                }
            }
        }
    }
}
