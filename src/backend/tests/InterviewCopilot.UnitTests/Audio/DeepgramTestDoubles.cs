using System.Threading.Channels;
using InterviewCopilot.Application.Audio;
using InterviewCopilot.Application.Common.Interfaces;
using InterviewCopilot.Application.Events;
using InterviewCopilot.Domain.Entities;

namespace InterviewCopilot.UnitTests.Audio;

internal sealed class FixedRouter(bool deepgram) : ISpeechToTextRouter
{
    public List<Guid> Forgotten { get; } = [];

    public ValueTask<bool> UsesDeepgramAsync(Guid sessionId, CancellationToken cancellationToken) => ValueTask.FromResult(deepgram);

    public void Forget(Guid sessionId) => Forgotten.Add(sessionId);
}

internal sealed record SentMessage(string Kind, byte[] Payload);

/// <summary>A scripted Deepgram socket: records what is sent and lets the test push results, close or drop it.</summary>
internal sealed class FakeDeepgramConnection : IDeepgramConnection
{
    private readonly Channel<string?> _incoming = Channel.CreateUnbounded<string?>();
    private readonly List<SentMessage> _sent = [];
    private bool _dropped;

    /// <summary>Messages the fake pushes (then closes normally) when it receives CloseStream, as Deepgram flushes.</summary>
    public IReadOnlyList<string> RepliesOnCloseStream { get; init; } = [];

    /// <summary>When true, CloseStream gets no reply at all, so the sink has to give up draining.</summary>
    public bool IgnoreCloseStream { get; init; }

    public bool Disposed { get; private set; }

    public IReadOnlyList<SentMessage> Sent
    {
        get
        {
            lock (_sent)
            {
                return [.. _sent];
            }
        }
    }

    public int AudioFrames => Sent.Count(m => m.Kind == "audio");

    public int KeepAlives => Sent.Count(m => m.Kind == "keepalive");

    public bool CloseStreamSent => Sent.Any(m => m.Kind == "close");

    public void Push(string json) => _incoming.Writer.TryWrite(json);

    public void CloseNormally() => _incoming.Writer.TryWrite(null);

    public void Drop()
    {
        _dropped = true;
        _incoming.Writer.TryComplete(new DeepgramConnectionException("dropped"));
    }

    public ValueTask SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken cancellationToken) =>
        Record("audio", pcm16.ToArray());

    public ValueTask SendKeepAliveAsync(CancellationToken cancellationToken) => Record("keepalive", []);

    public ValueTask SendCloseStreamAsync(CancellationToken cancellationToken)
    {
        var result = Record("close", []);
        if (!IgnoreCloseStream)
        {
            foreach (var reply in RepliesOnCloseStream)
            {
                Push(reply);
            }

            CloseNormally();
        }

        return result;
    }

    public async ValueTask<string?> ReceiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _incoming.Reader.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException ex)
        {
            throw new DeepgramConnectionException("dropped", ex);
        }
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    private ValueTask Record(string kind, byte[] payload)
    {
        if (_dropped)
        {
            throw new DeepgramConnectionException("dropped");
        }

        lock (_sent)
        {
            _sent.Add(new SentMessage(kind, payload));
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>Hands out scripted connections (or failures) in order and records every key it was asked to connect with.</summary>
internal sealed class FakeDeepgramFactory : IDeepgramConnectionFactory
{
    private readonly Queue<Func<IDeepgramConnection>> _script = new();
    private readonly List<string> _keys = [];

    public IReadOnlyList<string> Keys
    {
        get
        {
            lock (_keys)
            {
                return [.. _keys];
            }
        }
    }

    public int ConnectAttempts => Keys.Count;

    public List<FakeDeepgramConnection> Connections { get; } = [];

    public FakeDeepgramConnection Enqueue(FakeDeepgramConnection? connection = null)
    {
        connection ??= new FakeDeepgramConnection();
        _script.Enqueue(() => connection);
        Connections.Add(connection);
        return connection;
    }

    public void EnqueueFailure(Exception exception) => _script.Enqueue(() => throw exception);

    public ValueTask<IDeepgramConnection> ConnectAsync(string apiKey, CancellationToken cancellationToken)
    {
        lock (_keys)
        {
            _keys.Add(apiKey);
        }

        if (_script.Count == 0)
        {
            throw new DeepgramConnectionException("no more scripted connections");
        }

        return ValueTask.FromResult(_script.Dequeue()());
    }
}

internal sealed record PublishedEvent(Guid SessionId, string Type, object Data);

internal sealed class EventLog : ISessionEventPublisher
{
    private readonly List<PublishedEvent> _events = [];

    public IReadOnlyList<PublishedEvent> Events
    {
        get
        {
            lock (_events)
            {
                return [.. _events];
            }
        }
    }

    public IReadOnlyList<string> ErrorMessages =>
        [.. Events.Where(e => e.Type == SessionEventTypes.Error).Select(e => (string)e.Data.GetType().GetProperty("message")!.GetValue(e.Data)!)];

    public void Publish(Guid sessionId, string type, object data)
    {
        lock (_events)
        {
            _events.Add(new PublishedEvent(sessionId, type, data));
        }
    }
}

internal sealed class InMemoryPreferences : IUserPreferencesRepository
{
    public Dictionary<Guid, UserPreferences> Items { get; } = [];

    public Exception? Failure { get; set; }

    public Task<UserPreferences?> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        return Task.FromResult(Items.GetValueOrDefault(userId));
    }

    public void Add(UserPreferences preferences) => Items[preferences.UserId] = preferences;
}
