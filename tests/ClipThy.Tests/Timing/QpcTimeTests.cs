using ClipThy.Timing;

namespace ClipThy.Tests.Timing;

public class QpcTimeTests
{
    [Fact]
    public void From100ns_is_identity_at_10_MHz() =>
        Assert.Equal(123_456_789L, QpcTime.From100ns(123_456_789L, 10_000_000));

    [Fact]
    public void From100ns_scales_to_other_frequencies() =>
        Assert.Equal(3_000_000L, QpcTime.From100ns(10_000_000L, 3_000_000));

    [Fact]
    public void From100ns_does_not_overflow_after_a_month_of_uptime()
    {
        long thirtyDaysIn100ns = 30L * 24 * 3600 * 10_000_000;
        Assert.Equal(30L * 24 * 3600 * 24_000_000, QpcTime.From100ns(thirtyDaysIn100ns, 24_000_000));
    }

    [Fact]
    public void Audio_frames_round_trip_through_ticks()
    {
        long ticks = QpcTime.FromFrames(48_000, 48_000, 10_000_000);
        Assert.Equal(10_000_000L, ticks);
        Assert.Equal(48_000L, QpcTime.ToFrames(ticks, 48_000, 10_000_000));
    }

    [Fact]
    public void Seconds_convert_both_ways()
    {
        Assert.Equal(15_000_000L, QpcTime.FromSeconds(1.5, 10_000_000));
        Assert.Equal(1.5, QpcTime.ToSeconds(15_000_000, 10_000_000));
    }
}
