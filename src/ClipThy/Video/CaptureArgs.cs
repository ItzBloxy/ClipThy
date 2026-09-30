using System.Globalization;

namespace ClipThy.Video;

/// <summary>Command line for the long-running capture ffmpeg (video only, MPEG-TS to stdout).</summary>
public static class CaptureArgs
{
    /// <summary>The primary monitor, tagged with BT.709 primaries/transfer (output options can't set these).</summary>
    public static string DesktopGraph(int fps) =>
        $"ddagrab=output_idx=0:framerate={fps}:draw_mouse=1,setparams=color_primaries=bt709:color_trc=bt709";

    public static IReadOnlyList<string> Build(EncoderKind encoder, int fps, int bitrateMbps, string lavfiGraph)
    {
        string gop = fps.ToString(CultureInfo.InvariantCulture);
        string bitrate = $"{bitrateMbps * 1000}k";
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-nostats", "-loglevel", "warning",
            "-copyts", "-use_wallclock_as_timestamps", "1",
            "-f", "lavfi", "-i", lavfiGraph,
        };

        if (encoder == EncoderKind.Nvenc)
        {
            args.AddRange(
            [
                "-c:v", "h264_nvenc", "-preset", "p4", "-tune", "ll", "-rc", "vbr",
                "-b:v", bitrate, "-maxrate", $"{bitrateMbps * 1500}k", "-bufsize", $"{bitrateMbps * 2000}k",
                "-g", gop, "-bf", "0", "-delay", "0",
            ]);
        }
        else
        {
            args.AddRange(["-c:v", "h264_mf", "-hw_encoding", "1", "-scenario", "live_streaming", "-b:v", bitrate, "-g", gop]);
        }

        args.AddRange(["-muxdelay", "0", "-muxpreload", "0", "-f", "mpegts", "-flush_packets", "1", "pipe:1"]);
        return args;
    }
}
