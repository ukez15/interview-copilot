using System.Buffers.Binary;

namespace InterviewCopilot.Infrastructure.Transcription;

/// <summary>Wraps raw PCM16 mono audio in a WAV header, which Whisper accepts.</summary>
internal static class WavEncoder
{
    private const int HeaderSize = 44;
    private const short BitsPerSample = 16;
    private const short Channels = 1;

    public static byte[] Encode(ReadOnlySpan<byte> pcm16, int sampleRate)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Sample rate must be positive.");
        }

        var wav = new byte[HeaderSize + pcm16.Length];
        var span = wav.AsSpan();
        var byteRate = sampleRate * Channels * BitsPerSample / 8;

        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], HeaderSize - 8 + pcm16.Length);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], Channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], byteRate);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], (short)(Channels * BitsPerSample / 8));
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], BitsPerSample);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], pcm16.Length);
        pcm16.CopyTo(span[HeaderSize..]);
        return wav;
    }
}
