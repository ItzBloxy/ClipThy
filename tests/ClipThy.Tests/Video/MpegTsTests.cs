using ClipThy.Tests.TestSupport;
using ClipThy.Video;

namespace ClipThy.Tests.Video;

public class MpegTsTests
{
    private readonly TsBuilder _ts = new();

    [Fact]
    public void Reads_the_pmt_and_video_pids()
    {
        Assert.True(MpegTs.TryReadPmtPid(MpegTs.Payload(_ts.Pat()), out int pmtPid));
        Assert.Equal(TsBuilder.PmtPid, pmtPid);
        Assert.True(MpegTs.TryReadH264Pid(MpegTs.Payload(_ts.Pmt()), out int videoPid));
        Assert.Equal(TsBuilder.VideoPid, videoPid);
    }

    [Fact]
    public void Reads_pts_and_keyframe_flag()
    {
        byte[] frame = _ts.Frame(pts: 0x1_2345_6789, keyframe: true);
        ReadOnlySpan<byte> first = frame.AsSpan(0, MpegTs.PacketSize);

        Assert.Equal(TsBuilder.VideoPid, MpegTs.Pid(first));
        Assert.True(MpegTs.PayloadUnitStart(first));
        Assert.True(MpegTs.RandomAccess(first));
        Assert.True(MpegTs.TryReadPts(MpegTs.Payload(first), out long pts));
        Assert.Equal(0x1_2345_6789L, pts);
        Assert.False(MpegTs.RandomAccess(_ts.Frame(0, keyframe: false).AsSpan(0, MpegTs.PacketSize)));
    }

    [Fact]
    public void Utc_is_converted_to_the_90_kHz_wall_clock()
    {
        DateTime oneSecondAfterEpoch = DateTime.UnixEpoch.AddSeconds(1);
        Assert.Equal(90_000L, MpegTs.UtcTo90k(oneSecondAfterEpoch));
        Assert.InRange(MpegTs.UtcTo90k(DateTime.UtcNow), 0, MpegTs.PtsWrap - 1);
    }
}
