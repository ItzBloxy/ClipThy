using System.Diagnostics;
using ClipThy.Tests.TestSupport;
using ClipThy.Timing;
using ClipThy.Video;

namespace ClipThy.Tests.Integration;

/// <summary>
/// Needs a real display: runs the production desktop capture for ~8 s into memory only (frames are never written
/// anywhere; the test reads their timestamps and discards them).
/// </summary>
[Trait("Category", "Hardware")]
public class CapturePacingTests
{
    [Fact]
    public async Task Desktop_capture_keeps_a_steady_60_fps()
    {
        // ffmpeg's ddagrab paces frames with Sleep(); on the default 15.6 ms Windows timer about 4-5 % of 1/60 s slots
        // were skipped. VideoCapture gives ffmpeg a 1 ms timer, so no more than 1 % may be missing.
        var video = new VideoBuffer(Stopwatch.Frequency) { Retention = TimeSpan.FromSeconds(10) };
        using var capture = new VideoCapture(video, e => CaptureArgs.Build(e, 60, 20, CaptureArgs.DesktopGraph(60)), "ffmpeg", EncoderMode.Nvenc, job: null, log: _ => { });
        capture.Start();
        long started = Stopwatch.GetTimestamp();
        await Wait.Until(() => video.NewestCaptureQpc - started > QpcTime.FromSeconds(8, Stopwatch.Frequency), TimeSpan.FromSeconds(30));

        VideoSnapshot snapshot = video.Snapshot(video.NewestCaptureQpc!.Value, TimeSpan.FromSeconds(6))!;
        List<long> pts = FramePts(snapshot.Data);
        long missing = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            long slots = (long)Math.Round(((pts[i] - pts[i - 1]) % MpegTs.PtsWrap + MpegTs.PtsWrap) % MpegTs.PtsWrap / 1500.0);
            if (slots > 1) missing += slots - 1;
        }
        double missingPercent = 100.0 * missing / (pts.Count + missing);
        Assert.True(missingPercent <= 1.0, $"{missing} of {pts.Count + missing} frame slots were skipped ({missingPercent:F2} %)");
    }

    private static List<long> FramePts(byte[] ts)
    {
        var pts = new List<long>();
        for (int offset = 0; offset + MpegTs.PacketSize <= ts.Length; offset += MpegTs.PacketSize)
        {
            ReadOnlySpan<byte> packet = ts.AsSpan(offset, MpegTs.PacketSize);
            if (MpegTs.Pid(packet) == 0x100 && MpegTs.PayloadUnitStart(packet) && MpegTs.TryReadPts(MpegTs.Payload(packet), out long value))
            {
                pts.Add(value);
            }
        }
        return pts;
    }
}
