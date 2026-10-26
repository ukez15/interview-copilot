namespace InterviewCopilot.Application.Audio;

/// <summary>One open Deepgram live-transcription socket. Tests replace it with a fake.</summary>
public interface IDeepgramConnection : IAsyncDisposable
{
    /// <summary>Sends raw 16 kHz mono PCM16 LE audio as one binary message.</summary>
    /// <param name="pcm16">The audio payload.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>A task that completes when the audio is sent.</returns>
    /// <exception cref="DeepgramConnectionException">The socket dropped.</exception>
    ValueTask SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken cancellationToken);

    /// <summary>Sends the <c>KeepAlive</c> control message.</summary>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>A task that completes when the message is sent.</returns>
    /// <exception cref="DeepgramConnectionException">The socket dropped.</exception>
    ValueTask SendKeepAliveAsync(CancellationToken cancellationToken);

    /// <summary>Sends the <c>CloseStream</c> control message, asking Deepgram to flush and finish.</summary>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>A task that completes when the message is sent.</returns>
    /// <exception cref="DeepgramConnectionException">The socket dropped.</exception>
    ValueTask SendCloseStreamAsync(CancellationToken cancellationToken);

    /// <summary>Reads the next text message (a JSON document).</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The message, or null once Deepgram closed the socket normally.</returns>
    /// <exception cref="DeepgramConnectionException">The socket dropped.</exception>
    ValueTask<string?> ReceiveAsync(CancellationToken cancellationToken);
}

/// <summary>Opens Deepgram live-transcription sockets.</summary>
public interface IDeepgramConnectionFactory
{
    /// <summary>Opens a socket authenticated with the user's key.</summary>
    /// <param name="apiKey">The user's Deepgram key. Never logged or put in an exception message.</param>
    /// <param name="cancellationToken">Cancels the connect.</param>
    /// <returns>The open connection.</returns>
    /// <exception cref="DeepgramRejectedKeyException">Deepgram answered 401.</exception>
    /// <exception cref="DeepgramConnectionException">The socket could not be opened.</exception>
    ValueTask<IDeepgramConnection> ConnectAsync(string apiKey, CancellationToken cancellationToken);
}

/// <summary>The Deepgram socket could not be opened, or dropped. Messages never contain the key or transcript text.</summary>
public class DeepgramConnectionException : Exception
{
    /// <summary>Creates the exception with a default message.</summary>
    public DeepgramConnectionException()
        : this("The connection to Deepgram failed.")
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">A message without the key or transcript text.</param>
    public DeepgramConnectionException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">A message without the key or transcript text.</param>
    /// <param name="innerException">The underlying failure.</param>
    public DeepgramConnectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Deepgram answered 401 when the socket was opened.</summary>
public sealed class DeepgramRejectedKeyException : DeepgramConnectionException
{
    /// <summary>Creates the exception.</summary>
    public DeepgramRejectedKeyException()
        : base("Deepgram rejected the API key.")
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">A message without the key.</param>
    public DeepgramRejectedKeyException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">A message without the key.</param>
    /// <param name="innerException">The underlying failure.</param>
    public DeepgramRejectedKeyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
