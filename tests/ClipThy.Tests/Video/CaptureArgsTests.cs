using ClipThy.Tests.TestSupport;
using ClipThy.Video;

namespace ClipThy.Tests.Video;

public class CaptureArgsTests
{
    [Fact]
    public void Nvenc_capture_uses_wall_clock_timestamps_and_low_latency_settings()
    {
        IReadOnlyList<string> args = CaptureArgs.Build(EncoderKind.Nvenc, 60, 20, CaptureArgs.DesktopGraph(60));

        Assert.Contains("-copyts", args);
        Assert.True(ArgsAssert.HasPair(args, "-use_wallclock_as_timestamps", "1"));
        Assert.True(ArgsAssert.HasPair(args, "-i",
            "ddagrab=output_idx=0:framerate=60:draw_mouse=1,setparams=color_primaries=bt709:color_trc=bt709"));
        Assert.True(ArgsAssert.HasPair(args, "-c:v", "h264_nvenc"));
        Assert.True(ArgsAssert.HasPair(args, "-b:v", "20000k"));
        Assert.True(ArgsAssert.HasPair(args, "-maxrate", "30000k"));
        Assert.True(ArgsAssert.HasPair(args, "-bufsize", "40000k"));
        Assert.True(ArgsAssert.HasPair(args, "-g", "60"));
        Assert.True(ArgsAssert.HasPair(args, "-bf", "0"));
        Assert.True(ArgsAssert.HasPair(args, "-delay", "0"));
        Assert.True(ArgsAssert.HasPair(args, "-muxdelay", "0"));
        Assert.True(ArgsAssert.HasPair(args, "-f", "mpegts"));
        Assert.Equal("pipe:1", args[^1]);
    }

    [Fact]
    public void Media_foundation_capture_refuses_software_encoding()
    {
        IReadOnlyList<string> args = CaptureArgs.Build(EncoderKind.MediaFoundation, 30, 10, "testsrc2");

        Assert.True(ArgsAssert.HasPair(args, "-c:v", "h264_mf"));
        Assert.True(ArgsAssert.HasPair(args, "-hw_encoding", "1"));
        Assert.True(ArgsAssert.HasPair(args, "-b:v", "10000k"));
        Assert.True(ArgsAssert.HasPair(args, "-g", "30"));
        Assert.DoesNotContain("-delay", args);
    }
}
