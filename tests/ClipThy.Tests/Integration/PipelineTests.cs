using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ClipThy.Audio;
using ClipThy.Config;
using ClipThy.Export;
using ClipThy.Tests.TestSupport;
using ClipThy.Timing;
using ClipThy.Video;

namespace ClipThy.Tests.Integration;

/// <summary>Real ffmpeg and hardware encoder, with a test pattern instead of the screen.</summary>
[Trait("Category", "Integration")]
public class PipelineTests
{
    // NVENC gets BGRA like ddagrab, so it takes the same RGB path (and writes the same colour tags) as on the
    // desktop. Media Foundation's software input only negotiates NV12 (on the desktop it gets GPU frames).
    private static string TestGraph(EncoderKind encoder) =>
        $"testsrc2=size=640x360:rate=60,realtime,format={(encoder == EncoderKind.Nvenc ? "bgra" : "nv12")}," +
        "setparams=color_primaries=bt709:color_trc=bt709";

    private static byte[] Tone(int frames, double frequency)
    {
        var pcm = new byte[frames * AudioBuffer.BytesPerFrame];
        for (int i = 0; i < frames; i++)
        {
            short sample = (short)(Math.Sin(2 * Math.PI * frequency * i / AudioBuffer.SampleRate) * 8000);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4), sample);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 2), sample);
        }
        return pcm;
    }

    [Theory]
    [InlineData(EncoderKind.Nvenc, SaveMode.Mixed)]
    [InlineData(EncoderKind.Nvenc, SaveMode.Multitrack)]
    [InlineData(EncoderKind.MediaFoundation, SaveMode.Mixed)]
    [InlineData(EncoderKind.MediaFoundation, SaveMode.Multitrack)]
    public async Task Records_and_exports_a_five_second_clip(EncoderKind encoder, SaveMode mode)
    {
        using var dir = new TempDir();
        long frequency = Stopwatch.Frequency;
        var video = new VideoBuffer(frequency) { Retention = TimeSpan.FromSeconds(7) };
        var system = new AudioBuffer(frequency);
        var mic = new AudioBuffer(frequency);
        EncoderMode encoderMode = encoder == EncoderKind.Nvenc ? EncoderMode.Nvenc : EncoderMode.MediaFoundation;
        using var capture = new VideoCapture(video, e => CaptureArgs.Build(e, 60, 4, TestGraph(e)), "ffmpeg", encoderMode, job: null, log: _ => { });
        capture.Start();

        long started = Stopwatch.GetTimestamp();
        await Wait.Until(() => video.NewestCaptureQpc - started > QpcTime.FromSeconds(7, frequency), TimeSpan.FromSeconds(30));
        long now = Stopwatch.GetTimestamp();
        system.Append(Tone(8 * AudioBuffer.SampleRate, 440), now - QpcTime.FromSeconds(8, frequency));
        mic.Append(Tone(8 * AudioBuffer.SampleRate, 880), now - QpcTime.FromSeconds(8, frequency));

        var exporter = new ClipExporter(video, system, mic, new ProcessFfmpegRunner(() => "ffmpeg"), SystemClock.Instance,
            () => new AppSettings { ClipSeconds = 5, Fps = 60, OutputRoot = dir.File("out") }, dir.File("temp"));
        var done = new TaskCompletionSource<SaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        exporter.Completed += result => done.TrySetResult(result);
        Assert.Equal(SaveRequestOutcome.Accepted, exporter.RequestSave(mode));
        SaveResult saved = await done.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(saved.Success, saved.Error);
        JsonElement probe = FfmpegTool.Probe(saved.Path!);
        double duration = double.Parse(probe.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        Assert.InRange(duration, 5.0, 6.2);

        JsonElement[] streams = probe.GetProperty("streams").EnumerateArray().ToArray();
        JsonElement videoStream = streams.Single(s => s.GetProperty("codec_type").GetString() == "video");
        Assert.Equal("h264", videoStream.GetProperty("codec_name").GetString());
        if (encoder == EncoderKind.Nvenc)
        {
            Assert.Equal("bt470bg", videoStream.GetProperty("color_space").GetString());
            Assert.Equal("bt709", videoStream.GetProperty("color_primaries").GetString());
            Assert.Equal("bt709", videoStream.GetProperty("color_transfer").GetString());
        }

        JsonElement[] audio = streams.Where(s => s.GetProperty("codec_type").GetString() == "audio").ToArray();
        Assert.All(audio, s => Assert.Equal("aac", s.GetProperty("codec_name").GetString()));
        string[] handlers = audio.Select(s => s.GetProperty("tags").GetProperty("handler_name").GetString()!).ToArray();
        int[] defaults = audio.Select(s => s.GetProperty("disposition").GetProperty("default").GetInt32()).ToArray();
        Assert.Equal(mode == SaveMode.Mixed ? new[] { "Mix" } : new[] { "Mix", "System", "Mic" }, handlers);
        Assert.Equal(mode == SaveMode.Mixed ? new[] { 1 } : new[] { 1, 0, 0 }, defaults);

        List<string> boxes = FfmpegTool.TopLevelBoxes(saved.Path!);
        Assert.True(boxes.IndexOf("moov") < boxes.IndexOf("mdat"), "moov must come before mdat (faststart)");
    }

    [Fact]
    public async Task Capture_restarts_after_ffmpeg_dies()
    {
        var video = new VideoBuffer(Stopwatch.Frequency);
        var states = new List<CaptureState>();
        using var capture = new VideoCapture(video, e => CaptureArgs.Build(e, 60, 4, TestGraph(e)), "ffmpeg", EncoderMode.Nvenc, job: null, log: _ => { });
        capture.StatusChanged += status => { lock (states) states.Add(status.State); };
        capture.Start();
        await Wait.Until(() => video.NewestCaptureQpc is not null, TimeSpan.FromSeconds(15));
        int firstPid = capture.CurrentProcessId!.Value;

        long killedAt = Stopwatch.GetTimestamp();
        Process.GetProcessById(firstPid).Kill();

        await Wait.Until(
            () => capture.CurrentProcessId is int pid && pid != firstPid && video.NewestCaptureQpc > killedAt,
            TimeSpan.FromSeconds(15));
        lock (states) Assert.Contains(CaptureState.Restarting, states);
        Assert.Equal(0, video.TimestampWarnings);
    }

    [Fact]
    public async Task A_clip_across_the_pts_wrap_exports_cleanly()
    {
        using var dir = new TempDir();
        string wrapTs = dir.File("wrap.ts");
        double wrapSeconds = MpegTs.PtsWrap / (double)MpegTs.ClockRate;
        FfmpegTool.Run("ffmpeg", "-hide_banner", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=60", "-t", "6",
            "-c:v", "h264_nvenc", "-g", "60", "-bf", "0",
            "-output_ts_offset", (wrapSeconds - 3).ToString("F3", CultureInfo.InvariantCulture),
            "-muxdelay", "0", "-muxpreload", "0", "-f", "mpegts", wrapTs);

        // Replay the file as if it arrived live, each frame 50 ms after its PTS.
        var buffer = new VideoBuffer(MpegTs.ClockRate) { Retention = TimeSpan.FromSeconds(10) };
        byte[] ts = File.ReadAllBytes(wrapTs);
        long frame = -1, arrivalQpc = 0, arrivalUtc90k = 0;
        for (int offset = 0; offset + MpegTs.PacketSize <= ts.Length; offset += MpegTs.PacketSize)
        {
            ReadOnlySpan<byte> packet = ts.AsSpan(offset, MpegTs.PacketSize);
            if (MpegTs.Pid(packet) == 0x100 && MpegTs.PayloadUnitStart(packet) && MpegTs.TryReadPts(MpegTs.Payload(packet), out long pts))
            {
                frame++;
                arrivalQpc = 1_000_000 + frame * 1500 + 4500;
                arrivalUtc90k = (pts + 4500) % MpegTs.PtsWrap;
            }
            buffer.Append(packet, arrivalQpc, arrivalUtc90k);
        }
        Assert.Equal(0, buffer.TimestampWarnings);

        VideoSnapshot snapshot = buffer.Snapshot(buffer.NewestCaptureQpc!.Value, TimeSpan.FromSeconds(4))!;
        string cutTs = dir.File("cut.ts");
        string cutMp4 = dir.File("cut.mp4");
        await File.WriteAllBytesAsync(cutTs, snapshot.Data);
        FfmpegTool.Run("ffmpeg", "-hide_banner", "-v", "error", "-i", cutTs, "-c", "copy", "-y", cutMp4);

        int frames = int.Parse(FfmpegTool.Run("ffprobe", "-v", "error", "-count_frames", "-select_streams", "v:0",
            "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0", cutMp4).Trim(), CultureInfo.InvariantCulture);
        double duration = double.Parse(FfmpegTool.Run("ffprobe", "-v", "error", "-show_entries", "format=duration",
            "-of", "csv=p=0", cutMp4).Trim(), CultureInfo.InvariantCulture);
        Assert.Equal(snapshot.FrameCount, frames);
        Assert.InRange(duration, snapshot.FrameCount / 60.0 - 0.05, snapshot.FrameCount / 60.0 + 0.05);
    }
}
