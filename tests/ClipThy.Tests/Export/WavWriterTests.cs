using System.Text;
using ClipThy.Export;

namespace ClipThy.Tests.Export;

public class WavWriterTests
{
    [Fact]
    public void Writes_a_canonical_pcm_header_followed_by_the_samples()
    {
        byte[] pcm = [1, 2, 3, 4, 5, 6, 7, 8];
        using var stream = new MemoryStream();

        WavWriter.Write(stream, pcm, 48_000, channels: 2, bitsPerSample: 16);

        byte[] wav = stream.ToArray();
        Assert.Equal(44 + pcm.Length, wav.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(36 + pcm.Length, BitConverter.ToInt32(wav, 4));
        Assert.Equal("WAVEfmt ", Encoding.ASCII.GetString(wav, 8, 8));
        Assert.Equal((short)1, BitConverter.ToInt16(wav, 20));
        Assert.Equal((short)2, BitConverter.ToInt16(wav, 22));
        Assert.Equal(48_000, BitConverter.ToInt32(wav, 24));
        Assert.Equal(192_000, BitConverter.ToInt32(wav, 28));
        Assert.Equal((short)4, BitConverter.ToInt16(wav, 32));
        Assert.Equal((short)16, BitConverter.ToInt16(wav, 34));
        Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal(pcm.Length, BitConverter.ToInt32(wav, 40));
        Assert.Equal(pcm, wav[44..]);
    }
}
