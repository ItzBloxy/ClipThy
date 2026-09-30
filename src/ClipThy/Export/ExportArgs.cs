namespace ClipThy.Export;

public enum SaveMode
{
    /// <summary>Alt+F10: one audio track with system audio and mic mixed.</summary>
    Mixed,

    /// <summary>Alt+F11: track 1 Mix (default), track 2 System, track 3 Mic.</summary>
    Multitrack,
}

/// <summary>Command line for the per-clip export ffmpeg: copies video, mixes and encodes audio.</summary>
public static class ExportArgs
{
    public const string MixFilter =
        "[1:a][2:a]amix=inputs=2:normalize=0,alimiter=limit=0.98:level=0:latency=1[mix]";

    public static IReadOnlyList<string> Build(SaveMode mode, string videoTs, string systemWav, string micWav, string outputMp4)
    {
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-loglevel", "error",
            "-i", videoTs, "-i", systemWav, "-i", micWav,
            "-filter_complex", MixFilter,
            "-map", "0:v", "-map", "[mix]",
        };
        if (mode == SaveMode.Multitrack) args.AddRange(["-map", "1:a", "-map", "2:a"]);

        args.AddRange(["-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-metadata:s:a:0", "handler_name=Mix"]);
        if (mode == SaveMode.Multitrack)
        {
            args.AddRange(
            [
                "-metadata:s:a:1", "handler_name=System", "-metadata:s:a:2", "handler_name=Mic",
                "-disposition:a:0", "default", "-disposition:a:1", "0", "-disposition:a:2", "0",
            ]);
        }

        args.AddRange(["-movflags", "+faststart", "-y", outputMp4]);
        return args;
    }
}
