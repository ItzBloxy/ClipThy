using ClipThy.Export;
using ClipThy.Tests.TestSupport;

namespace ClipThy.Tests.Export;

public class ExportArgsTests
{
    [Fact]
    public void Mixed_export_has_one_mixed_track()
    {
        IReadOnlyList<string> args = ExportArgs.Build(SaveMode.Mixed, "v.ts", "s.wav", "m.wav", "out.mp4");

        Assert.True(ArgsAssert.HasPair(args, "-filter_complex",
            "[1:a][2:a]amix=inputs=2:normalize=0,alimiter=limit=0.98:level=0:latency=1[mix]"));
        Assert.True(ArgsAssert.HasPair(args, "-map", "0:v"));
        Assert.True(ArgsAssert.HasPair(args, "-map", "[mix]"));
        Assert.False(ArgsAssert.HasPair(args, "-map", "1:a"));
        Assert.True(ArgsAssert.HasPair(args, "-c:v", "copy"));
        Assert.True(ArgsAssert.HasPair(args, "-metadata:s:a:0", "handler_name=Mix"));
        Assert.True(ArgsAssert.HasPair(args, "-movflags", "+faststart"));
        Assert.Equal("out.mp4", args[^1]);
    }

    [Fact]
    public void Multitrack_export_adds_system_and_mic_tracks()
    {
        IReadOnlyList<string> args = ExportArgs.Build(SaveMode.Multitrack, "v.ts", "s.wav", "m.wav", "out.mp4");

        Assert.True(ArgsAssert.HasPair(args, "-map", "1:a"));
        Assert.True(ArgsAssert.HasPair(args, "-map", "2:a"));
        Assert.True(ArgsAssert.HasPair(args, "-metadata:s:a:1", "handler_name=System"));
        Assert.True(ArgsAssert.HasPair(args, "-metadata:s:a:2", "handler_name=Mic"));
        Assert.True(ArgsAssert.HasPair(args, "-disposition:a:0", "default"));
        Assert.True(ArgsAssert.HasPair(args, "-disposition:a:1", "0"));
        Assert.True(ArgsAssert.HasPair(args, "-disposition:a:2", "0"));
    }
}
