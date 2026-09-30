using ClipThy.Tests.TestSupport;
using ClipThy.Video;

namespace ClipThy.Tests.Video;

public class VideoBufferTests
{
    private const long Frequency = MpegTs.ClockRate; // 1 QPC tick = 1 PTS tick, so expectations are exact
    private const long FrameTicks = 1500;             // 60 fps
    private const long Delay = 4500;                  // frames reach ClipThy 50 ms after capture
    private const long QpcBase = 1_000_000;
    private const long PtsBase = 5_000_000;

    private readonly TsBuilder _ts = new();

    /// <summary>Frame n is captured at QpcBase + n frames, has PTS PtsBase + n frames, and arrives Delay later.</summary>
    private static long Capture(int n) => QpcBase + n * FrameTicks;

    private void AddFrames(VideoBuffer buffer, int from, int count, long ptsBase = PtsBase, int packetsPerFrame = TsBuilder.PacketsPerFrame)
    {
        for (int n = from; n < from + count; n++)
        {
            long pts = (ptsBase + n * FrameTicks) % MpegTs.PtsWrap;
            byte[] frame = _ts.Frame(pts, keyframe: n % 60 == 0, packetsPerFrame);
            buffer.Append(frame, arrivalQpc: Capture(n) + Delay, arrivalUtc90k: (pts + Delay) % MpegTs.PtsWrap);
        }
    }

    private VideoBuffer NewBuffer(double retentionSeconds = 60)
    {
        var buffer = new VideoBuffer(Frequency) { Retention = TimeSpan.FromSeconds(retentionSeconds) };
        buffer.Append(_ts.Pat(), 0, 0);
        buffer.Append(_ts.Pmt(), 0, 0);
        return buffer;
    }

    [Fact]
    public void Newest_capture_time_is_that_of_the_last_complete_frame()
    {
        VideoBuffer buffer = NewBuffer();

        AddFrames(buffer, 0, 3);

        Assert.Equal(Capture(1), buffer.NewestCaptureQpc);
        Assert.Equal(0, buffer.TimestampWarnings);
    }

    [Fact]
    public void Capture_times_survive_the_33_bit_pts_wrap()
    {
        VideoBuffer buffer = NewBuffer();

        AddFrames(buffer, 0, 4, ptsBase: MpegTs.PtsWrap - 2 * FrameTicks); // frame 2 has PTS 0

        Assert.Equal(Capture(2), buffer.NewestCaptureQpc);
        Assert.Equal(0, buffer.TimestampWarnings);
    }

    [Fact]
    public void A_frame_stamped_slightly_ahead_of_our_clock_is_still_mapped()
    {
        VideoBuffer buffer = NewBuffer();
        for (int n = 0; n < 2; n++)
        {
            long pts = PtsBase + n * FrameTicks;
            // ffmpeg's clock runs 20 ms ahead of ours, so the frame "arrives" before its timestamp.
            buffer.Append(_ts.Frame(pts, keyframe: n == 0), arrivalQpc: Capture(n), arrivalUtc90k: pts - 1800);
        }

        Assert.Equal(Capture(0) + 1800, buffer.NewestCaptureQpc);
        Assert.Equal(0, buffer.TimestampWarnings);
    }

    [Theory]
    [InlineData(10.0)]
    [InlineData(-2.0)]
    public void An_implausible_delay_falls_back_to_the_arrival_time(double delaySeconds)
    {
        VideoBuffer buffer = NewBuffer();
        for (int n = 0; n < 2; n++)
        {
            long pts = PtsBase + n * FrameTicks;
            long arrivalUtc90k = pts + (long)(delaySeconds * MpegTs.ClockRate);
            buffer.Append(_ts.Frame(pts, keyframe: n == 0), arrivalQpc: Capture(n), arrivalUtc90k);
        }

        Assert.Equal(Capture(0), buffer.NewestCaptureQpc);
        Assert.Equal(2, buffer.TimestampWarnings);
    }

    [Fact]
    public void Unaligned_reads_give_the_same_snapshot_as_whole_packets()
    {
        VideoBuffer aligned = NewBuffer();
        AddFrames(aligned, 0, 130);

        // Same bytes, delivered in 100-byte pieces with one arrival time that maps every frame to the same capture time.
        var builder = new TsBuilder();
        var stream = new List<byte>(builder.Pat().Concat(builder.Pmt()));
        for (int n = 0; n < 130; n++) stream.AddRange(builder.Frame(PtsBase + n * FrameTicks, n % 60 == 0));
        byte[] bytes = stream.ToArray();
        var unaligned = new VideoBuffer(Frequency) { Retention = TimeSpan.FromSeconds(60) };
        for (int i = 0; i < bytes.Length; i += 100)
        {
            unaligned.Append(bytes.AsSpan(i, Math.Min(100, bytes.Length - i)), QpcBase + 200_000, PtsBase + 200_000);
        }

        VideoSnapshot? expected = aligned.Snapshot(Capture(120), TimeSpan.FromSeconds(1));
        VideoSnapshot? actual = unaligned.Snapshot(Capture(120), TimeSpan.FromSeconds(1));

        Assert.NotNull(expected);
        Assert.NotNull(actual);
        Assert.Equal(expected.Data, actual.Data);
        Assert.Equal(expected.FirstFrameQpc, actual.FirstFrameQpc);
    }

    [Fact]
    public void Snapshot_starts_at_the_last_keyframe_before_the_requested_start()
    {
        VideoBuffer buffer = NewBuffer();
        AddFrames(buffer, 0, 180); // keyframes at 0, 60, 120

        VideoSnapshot? snapshot = buffer.Snapshot(endQpc: Capture(170), length: TimeSpan.FromSeconds(1.5));

        Assert.NotNull(snapshot);
        Assert.Equal(Capture(60), snapshot.FirstFrameQpc); // 1.5 s before frame 170 is frame 80
        Assert.Equal(Capture(170), snapshot.LastFrameQpc);
        Assert.Equal(111, snapshot.FrameCount);
        Assert.Equal((2 + 111 * TsBuilder.PacketsPerFrame) * MpegTs.PacketSize, snapshot.Data.Length);
        Assert.Equal(0, MpegTs.Pid(snapshot.Data.AsSpan(0, MpegTs.PacketSize)));
        Assert.Equal(TsBuilder.PmtPid, MpegTs.Pid(snapshot.Data.AsSpan(MpegTs.PacketSize, MpegTs.PacketSize)));
        Assert.True(MpegTs.RandomAccess(snapshot.Data.AsSpan(2 * MpegTs.PacketSize, MpegTs.PacketSize)));
    }

    [Fact]
    public void Snapshot_uses_the_oldest_keyframe_when_less_is_buffered()
    {
        VideoBuffer buffer = NewBuffer();
        AddFrames(buffer, 0, 90);

        VideoSnapshot? snapshot = buffer.Snapshot(Capture(88), TimeSpan.FromSeconds(30));

        Assert.NotNull(snapshot);
        Assert.Equal(Capture(0), snapshot.FirstFrameQpc);
        Assert.Equal(89, snapshot.FrameCount);
    }

    [Fact]
    public void Snapshot_leaves_out_the_newest_frame_when_the_press_is_after_it()
    {
        VideoBuffer buffer = NewBuffer();
        AddFrames(buffer, 0, 10);

        VideoSnapshot? snapshot = buffer.Snapshot(Capture(50), TimeSpan.FromSeconds(1));

        Assert.NotNull(snapshot);
        Assert.Equal(9, snapshot.FrameCount); // frame 9 may still be arriving
        Assert.Equal(Capture(8), snapshot.LastFrameQpc);
    }

    [Fact]
    public void Snapshot_is_null_without_a_keyframe_or_stream_tables()
    {
        VideoBuffer noKeyframe = NewBuffer();
        for (int n = 0; n < 5; n++)
        {
            long pts = PtsBase + n * FrameTicks;
            noKeyframe.Append(_ts.Frame(pts, keyframe: false), Capture(n) + Delay, pts + Delay);
        }
        var noTables = new VideoBuffer(Frequency);
        AddFrames(noTables, 0, 5);

        Assert.Null(noKeyframe.Snapshot(Capture(4), TimeSpan.FromSeconds(1)));
        Assert.Null(noTables.Snapshot(Capture(4), TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Old_frames_are_trimmed_back_to_a_keyframe()
    {
        VideoBuffer buffer = NewBuffer(retentionSeconds: 1);

        AddFrames(buffer, 0, 300); // 5 s

        VideoSnapshot? snapshot = buffer.Snapshot(Capture(298), TimeSpan.FromSeconds(10));
        Assert.NotNull(snapshot);
        Assert.Equal(Capture(180), snapshot.FirstFrameQpc);
    }

    [Fact]
    public void Large_streams_span_several_storage_blocks()
    {
        VideoBuffer buffer = NewBuffer(retentionSeconds: 2);

        AddFrames(buffer, 0, 600, packetsPerFrame: 40); // 24,000 packets

        VideoSnapshot? snapshot = buffer.Snapshot(Capture(598), TimeSpan.FromSeconds(1.5));
        Assert.NotNull(snapshot);
        Assert.Equal(Capture(480), snapshot.FirstFrameQpc);
        Assert.Equal(119, snapshot.FrameCount);
        Assert.Equal((2 + 119 * 40) * MpegTs.PacketSize, snapshot.Data.Length);
        Assert.True(MpegTs.RandomAccess(snapshot.Data.AsSpan(2 * MpegTs.PacketSize, MpegTs.PacketSize)));
    }

    [Fact]
    public void Clear_forgets_everything()
    {
        VideoBuffer buffer = NewBuffer();
        AddFrames(buffer, 0, 70);

        buffer.Clear();

        Assert.Null(buffer.NewestCaptureQpc);
        Assert.Null(buffer.Snapshot(Capture(69), TimeSpan.FromSeconds(1)));
    }
}
