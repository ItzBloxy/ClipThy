namespace ClipThy.Video;

public enum EncoderKind
{
    Nvenc,
    MediaFoundation,
}

/// <summary>The "encoder" setting: auto = NVENC with Media Foundation fallback.</summary>
public enum EncoderMode
{
    Auto,
    Nvenc,
    MediaFoundation,
}

/// <summary>Encoder choice, NVENC failure detection and restart backoff for the capture ffmpeg.</summary>
public static class EncoderPolicy
{
    private static readonly string[] NvencStartupFailureMarkers =
    [
        "Driver does not support the required nvenc API version",
        "minimum required Nvidia driver",
        "Cannot load nvEncodeAPI",
        "No capable devices found",
        "OpenEncodeSessionEx failed",
        "Could not open encoder",
        "Error while opening encoder",
    ];

    private static readonly TimeSpan[] BackoffSteps =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
    ];

    public static EncoderMode ParseMode(string setting) => setting switch
    {
        "nvenc" => EncoderMode.Nvenc,
        "mf" => EncoderMode.MediaFoundation,
        _ => EncoderMode.Auto,
    };

    public static EncoderKind Initial(EncoderMode mode) =>
        mode == EncoderMode.MediaFoundation ? EncoderKind.MediaFoundation : EncoderKind.Nvenc;

    public static bool IsNvencStartupFailure(IEnumerable<string> stderrLines) =>
        stderrLines.Any(line => line.Contains("nvenc", StringComparison.OrdinalIgnoreCase)
            && NvencStartupFailureMarkers.Any(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase)));

    /// <summary>True when a failed run means this session should switch to Media Foundation.</summary>
    public static bool ShouldFallBack(EncoderMode mode, EncoderKind current, bool producedPackets, IEnumerable<string> stderrLines) =>
        mode == EncoderMode.Auto && current == EncoderKind.Nvenc && !producedPackets && IsNvencStartupFailure(stderrLines);

    /// <summary>Wait before restart after <paramref name="consecutiveFailures"/> failures in a row (1-based).</summary>
    public static TimeSpan Backoff(int consecutiveFailures) =>
        BackoffSteps[Math.Clamp(consecutiveFailures, 1, BackoffSteps.Length) - 1];
}
