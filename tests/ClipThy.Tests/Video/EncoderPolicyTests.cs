using ClipThy.Video;

namespace ClipThy.Tests.Video;

public class EncoderPolicyTests
{
    private static readonly string[] OldDriverStderr =
    [
        "[h264_nvenc @ 000001ffcc67f500] Driver does not support the required nvenc API version. Required: 13.0 Found: 12.2",
        "[h264_nvenc @ 000001ffcc67f500] The minimum required Nvidia driver for nvenc is 570.0 or newer",
        "[vost#0:0/h264_nvenc @ 000001ffcc67ef80] [enc:h264_nvenc @ 000001ffcc628e80] Could not open encoder before EOF",
    ];

    [Theory]
    [InlineData("auto", EncoderMode.Auto)]
    [InlineData("nvenc", EncoderMode.Nvenc)]
    [InlineData("mf", EncoderMode.MediaFoundation)]
    [InlineData("anything else", EncoderMode.Auto)]
    public void Parses_the_encoder_setting(string setting, EncoderMode expected) =>
        Assert.Equal(expected, EncoderPolicy.ParseMode(setting));

    [Fact]
    public void Starts_with_nvenc_unless_media_foundation_is_forced()
    {
        Assert.Equal(EncoderKind.Nvenc, EncoderPolicy.Initial(EncoderMode.Auto));
        Assert.Equal(EncoderKind.Nvenc, EncoderPolicy.Initial(EncoderMode.Nvenc));
        Assert.Equal(EncoderKind.MediaFoundation, EncoderPolicy.Initial(EncoderMode.MediaFoundation));
    }

    [Fact]
    public void Recognises_real_nvenc_startup_failures() =>
        Assert.True(EncoderPolicy.IsNvencStartupFailure(OldDriverStderr));

    [Fact]
    public void Ignores_unrelated_errors()
    {
        Assert.False(EncoderPolicy.IsNvencStartupFailure(["[out#0/mpegts @ 0000] Error writing trailer: Broken pipe"]));
        Assert.False(EncoderPolicy.IsNvencStartupFailure(["[vost#0:0/h264_mf @ 0000] Error while opening encoder"]));
    }

    [Fact]
    public void Falls_back_only_in_auto_mode_after_nvenc_failed_to_start()
    {
        Assert.True(EncoderPolicy.ShouldFallBack(EncoderMode.Auto, EncoderKind.Nvenc, producedPackets: false, OldDriverStderr));
        Assert.False(EncoderPolicy.ShouldFallBack(EncoderMode.Nvenc, EncoderKind.Nvenc, producedPackets: false, OldDriverStderr));
        Assert.False(EncoderPolicy.ShouldFallBack(EncoderMode.Auto, EncoderKind.Nvenc, producedPackets: true, OldDriverStderr));
        Assert.False(EncoderPolicy.ShouldFallBack(EncoderMode.Auto, EncoderKind.MediaFoundation, producedPackets: false, OldDriverStderr));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 5)]
    [InlineData(4, 10)]
    [InlineData(5, 30)]
    [InlineData(12, 30)]
    public void Backoff_grows_to_thirty_seconds(int failures, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), EncoderPolicy.Backoff(failures));
}
