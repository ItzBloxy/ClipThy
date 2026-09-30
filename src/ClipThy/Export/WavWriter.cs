using System.Buffers.Binary;

namespace ClipThy.Export;

/// <summary>Writes plain PCM WAV files (44-byte header).</summary>
public static class WavWriter
{
    public static void Write(string path, ReadOnlySpan<byte> pcm, int sampleRate = 48_000, short channels = 2, short bitsPerSample = 16)
    {
        using FileStream file = File.Create(path);
        Write(file, pcm, sampleRate, channels, bitsPerSample);
    }

    public static void Write(Stream stream, ReadOnlySpan<byte> pcm, int sampleRate, short channels, short bitsPerSample)
    {
        int blockAlign = channels * bitsPerSample / 8;
        Span<byte> header = stackalloc byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], 36 + pcm.Length);
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(header[20..], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(header[22..], channels);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], sampleRate * blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(header[32..], (short)blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(header[34..], bitsPerSample);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..], pcm.Length);
        stream.Write(header);
        stream.Write(pcm);
    }
}
