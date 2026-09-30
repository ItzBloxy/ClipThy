using System.Runtime.InteropServices;
using ClipThy.Tray;

namespace ClipThy.Tests.Tray;

public class ChimeTests
{
    private static readonly short[] Samples = MemoryMarshal.Cast<byte, short>(Chime.ClipSaved()).ToArray();

    private static double Rms(double fromSeconds, double toSeconds)
    {
        int from = (int)(fromSeconds * Chime.SampleRate), to = (int)(toSeconds * Chime.SampleRate);
        double sum = 0;
        for (int i = from; i < to; i++) sum += (double)Samples[i] * Samples[i];
        return Math.Sqrt(sum / (to - from));
    }

    [Fact]
    public void Lasts_0_8_seconds_of_48_kHz_mono() => Assert.Equal((int)(0.8 * Chime.SampleRate), Samples.Length);

    [Fact]
    public void Is_soft_peaking_at_a_quarter_of_full_scale()
    {
        int peak = Samples.Max(s => Math.Abs((int)s));
        Assert.InRange(peak, (int)(0.24 * short.MaxValue), (int)(0.26 * short.MaxValue));
    }

    [Fact]
    public void Starts_and_ends_in_silence_so_it_does_not_click()
    {
        Assert.Equal(0, Samples[0]);
        Assert.All(Samples[^5..], s => Assert.InRange(s, (short)-100, (short)100));
    }

    [Fact]
    public void Rises_into_a_second_note()
    {
        // The second (higher) note starts at 0.12 s, so the sound gets louder again just after it.
        Assert.True(Rms(0.13, 0.15) > Rms(0.10, 0.12));
    }
}
