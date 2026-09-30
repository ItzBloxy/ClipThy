namespace ClipThy.Tray;

/// <summary>The "clip saved" sound: a soft rising fifth (C5 then G5), synthesised so there are no asset files.</summary>
public static class Chime
{
    public const int SampleRate = 48_000;

    private const double Peak = 0.25;        // a quarter of full scale: noticeable, not sharp
    private const double Attack = 0.008;     // 8 ms rise, so the notes don't click
    private const double Decay = 0.18;       // bell-like exponential fade
    private const double Length = 0.8;
    private static readonly (double Frequency, double Start)[] Notes = [(523.25, 0), (783.99, 0.12)];
    private static readonly (double Ratio, double Level)[] Partials = [(1, 1), (2, 0.2)]; // a little octave warmth

    /// <summary>48 kHz mono 16-bit PCM.</summary>
    public static byte[] ClipSaved()
    {
        int frames = (int)(Length * SampleRate);
        var mix = new double[frames];
        foreach (var (frequency, start) in Notes)
        {
            for (int i = (int)(start * SampleRate); i < frames; i++)
            {
                double t = (double)i / SampleRate - start;
                double envelope = t < Attack ? Math.Pow(Math.Sin(Math.PI / 2 * t / Attack), 2) : Math.Exp(-(t - Attack) / Decay);
                double sample = 0;
                foreach (var (ratio, level) in Partials)
                {
                    // Higher partials fade faster, like a real bell.
                    sample += level * Math.Sin(2 * Math.PI * frequency * ratio * t) * Math.Exp(-(ratio - 1) * t / Decay);
                }
                mix[i] += envelope * sample;
            }
        }

        double loudest = mix.Max(Math.Abs);
        int fadeOut = SampleRate / 50; // last 20 ms fade to silence
        var pcm = new byte[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            double tail = i >= frames - fadeOut ? (frames - i - 1) / (double)fadeOut : 1;
            short value = (short)Math.Round(mix[i] / loudest * Peak * tail * short.MaxValue);
            BitConverter.TryWriteBytes(pcm.AsSpan(i * 2), value);
        }
        return pcm;
    }
}
