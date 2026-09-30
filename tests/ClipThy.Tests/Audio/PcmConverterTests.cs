using System.Runtime.InteropServices;
using ClipThy.Audio;

namespace ClipThy.Tests.Audio;

public class PcmConverterTests
{
    private static byte[] Floats(params float[] values) => MemoryMarshal.AsBytes(values.AsSpan()).ToArray();

    private static short[] Shorts(ReadOnlySpan<byte> pcm) => MemoryMarshal.Cast<byte, short>(pcm).ToArray();

    [Fact]
    public void Float_stereo_at_48_kHz_is_only_quantized()
    {
        var converter = new PcmConverter(new InputFormat(48_000, 2, SampleFormat.Float32));

        short[] output = Shorts(converter.Convert(Floats(0.5f, -0.5f, 1f, -1.5f)));

        Assert.Equal(new short[] { 16384, -16384, 32767, -32767 }, output);
    }

    [Fact]
    public void Mono_pcm16_is_copied_to_both_channels()
    {
        var converter = new PcmConverter(new InputFormat(48_000, 1, SampleFormat.Pcm16));
        byte[] input = MemoryMarshal.AsBytes(new short[] { 16384, -16384 }.AsSpan()).ToArray();

        short[] output = Shorts(converter.Convert(input));

        Assert.Equal(new short[] { 16384, 16384, -16384, -16384 }, output);
    }

    [Fact]
    public void Surround_is_downmixed_to_stereo()
    {
        var converter = new PcmConverter(new InputFormat(48_000, 6, SampleFormat.Float32));

        // FL, FR, FC, LFE, BL, BR
        short[] output = Shorts(converter.Convert(Floats(0.1f, 0.2f, 0.3f, 0.9f, 0.4f, 0.5f)));

        Assert.InRange(output[0], (short)19490, (short)19500); // 0.1 + 0.707 * (0.3 + 0.4)
        Assert.InRange(output[1], (short)25085, (short)25095); // 0.2 + 0.707 * (0.3 + 0.5)
    }

    [Fact]
    public void Resampling_44100_to_48000_keeps_duration_and_has_no_seams()
    {
        var converter = new PcmConverter(new InputFormat(44_100, 1, SampleFormat.Float32));
        var output = new List<short>();
        int inputFrame = 0;
        for (int block = 0; block < 100; block++) // 1 s in 10 ms blocks
        {
            var samples = new float[441];
            for (int i = 0; i < samples.Length; i++, inputFrame++)
            {
                samples[i] = 0.5f * MathF.Sin(2 * MathF.PI * 1000 * inputFrame / 44_100f);
            }
            output.AddRange(Shorts(converter.Convert(Floats(samples))));
        }

        int frames = output.Count / 2;
        Assert.InRange(frames, 47_998, 48_000);
        int biggestStep = Enumerable.Range(1, frames - 1).Max(i => Math.Abs(output[2 * i] - output[2 * (i - 1)]));
        Assert.True(biggestStep < 2300, $"discontinuity of {biggestStep}"); // a 1 kHz sine at 0.5 moves at most ~2145 per sample
    }

    [Fact]
    public void Resampling_96000_to_48000_halves_the_frame_count()
    {
        var converter = new PcmConverter(new InputFormat(96_000, 2, SampleFormat.Float32));
        int total = 0;

        for (int block = 0; block < 10; block++) total += converter.Convert(Floats(new float[960 * 2])).Length / AudioBuffer.BytesPerFrame;

        Assert.InRange(total, 4798, 4800);
    }
}
