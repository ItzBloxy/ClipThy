using System.Diagnostics;

namespace ClipThy.Timing;

/// <summary>Source of "now": QPC ticks for the app's timeline, plus UTC for file names.</summary>
public interface IClock
{
    long QpcNow { get; }
    long Frequency { get; }
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public long QpcNow => Stopwatch.GetTimestamp();
    public long Frequency => Stopwatch.Frequency;
    public DateTime UtcNow => DateTime.UtcNow;
}

/// <summary>Conversions between QPC ticks and other units, written to avoid 64-bit overflow.</summary>
public static class QpcTime
{
    private const long HundredNsPerSecond = 10_000_000;

    public static long FromSeconds(double seconds, long frequency) => (long)Math.Round(seconds * frequency);

    public static long FromTimeSpan(TimeSpan span, long frequency) => FromSeconds(span.TotalSeconds, frequency);

    public static double ToSeconds(long ticks, long frequency) => (double)ticks / frequency;

    /// <summary>WASAPI reports capture times as QPC positions in 100-ns units.</summary>
    public static long From100ns(long hundredNs, long frequency) =>
        hundredNs / HundredNsPerSecond * frequency + hundredNs % HundredNsPerSecond * frequency / HundredNsPerSecond;

    /// <summary>Duration of <paramref name="frames"/> audio frames at <paramref name="rate"/> Hz, in ticks.</summary>
    public static long FromFrames(long frames, int rate, long frequency) =>
        frames / rate * frequency + frames % rate * frequency / rate;

    /// <summary>Number of audio frames at <paramref name="rate"/> Hz in <paramref name="ticks"/>, rounded to nearest.</summary>
    public static long ToFrames(long ticks, int rate, long frequency) => (long)Math.Round((double)ticks * rate / frequency);
}
