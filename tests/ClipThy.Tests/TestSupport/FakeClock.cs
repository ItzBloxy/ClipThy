using ClipThy.Timing;

namespace ClipThy.Tests.TestSupport;

/// <summary>A clock tests can move by hand. 1 tick = 1 ms.</summary>
public sealed class FakeClock : IClock
{
    private long _qpc;

    public long QpcNow
    {
        get => Interlocked.Read(ref _qpc);
        set => Interlocked.Exchange(ref _qpc, value);
    }

    public long Frequency => 1000;

    public DateTime UtcNow { get; set; } = new(2026, 9, 30, 14, 45, 12, DateTimeKind.Utc);

    public void Advance(long ticks) => Interlocked.Add(ref _qpc, ticks);
}
