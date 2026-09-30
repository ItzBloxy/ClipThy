using System.Globalization;
using ClipThy.Audio;
using ClipThy.Config;
using ClipThy.Export;
using ClipThy.Tests.TestSupport;
using ClipThy.Video;

namespace ClipThy.Tests.Export;

public class ClipExporterTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new() { QpcNow = 100_000 }; // 1 tick = 1 ms
    private readonly FakeVideo _video = new();
    private readonly FakeAudio _system = new();
    private readonly FakeAudio _mic = new();
    private readonly FakeRunner _runner = new();

    public void Dispose() => _dir.Dispose();

    private string OutputRoot => _dir.File("out");

    private string TempRoot => _dir.File("temp");

    private ClipExporter NewExporter() => new(
        _video, _system, _mic, _runner, _clock,
        () => new AppSettings { ClipSeconds = 30, Fps = 60, OutputRoot = OutputRoot },
        TempRoot,
        delay: d =>
        {
            _clock.Advance((long)d.TotalMilliseconds);
            return Task.CompletedTask;
        });

    private static async Task<SaveResult> SaveAndWait(ClipExporter exporter, SaveMode mode)
    {
        var done = new TaskCompletionSource<SaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        exporter.Completed += result => done.TrySetResult(result);
        Assert.Equal(SaveRequestOutcome.Accepted, exporter.RequestSave(mode));
        return await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Saves_the_clip_into_the_month_folder()
    {
        _video.Newest = () => _clock.QpcNow >= 100_050 ? 100_000 : 99_950; // catches up 50 ms after the press
        _video.Result = new VideoSnapshot([9, 9, 9], FirstFrameQpc: 70_000, LastFrameQpc: 99_990, FrameCount: 1800);

        SaveResult result = await SaveAndWait(NewExporter(), SaveMode.Mixed);

        Assert.True(result.Success, result.Error);
        DateTime local = _clock.UtcNow.ToLocalTime();
        string expectedPath = Path.Combine(
            OutputPaths.MonthFolder(OutputRoot, local),
            $"clip_{local.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture)}.mp4");
        Assert.Equal(expectedPath, result.Path);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(expectedPath));
        Assert.Equal((100_000L, TimeSpan.FromSeconds(30)), _video.Calls.Single());
        Assert.Equal((70_000L, 99_990L + 17), _system.Calls.Single()); // audio runs to the end of the last frame
        Assert.Equal(_system.Calls.Single(), _mic.Calls.Single());
        Assert.True(_runner.InputsExisted.Single());
        Assert.Empty(Directory.GetDirectories(TempRoot));
        Assert.Equal(30.007, result.LengthSeconds, precision: 3);
    }

    [Fact]
    public async Task Stops_waiting_for_video_after_two_seconds()
    {
        _video.Newest = () => 99_000; // never reaches the press time
        _video.Result = new VideoSnapshot([1], 70_000, 99_000, 1);

        SaveResult result = await SaveAndWait(NewExporter(), SaveMode.Mixed);

        Assert.True(result.Success, result.Error);
        Assert.Equal(100_000L, _video.Calls.Single().EndQpc);
        Assert.InRange(_clock.QpcNow, 102_000, 102_100);
    }

    [Fact]
    public async Task Reports_nothing_to_save_when_nothing_is_buffered()
    {
        SaveResult result = await SaveAndWait(NewExporter(), SaveMode.Mixed);

        Assert.Equal(ClipExporter.NothingToSave, result.Error);
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task Refuses_a_fourth_save_while_three_are_pending()
    {
        _video.Newest = () => 200_000;
        _video.Result = new VideoSnapshot([1], 70_000, 99_990, 1);
        _runner.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ClipExporter exporter = NewExporter();
        int completed = 0;
        exporter.Completed += _ => Interlocked.Increment(ref completed);

        for (int i = 0; i < 3; i++) Assert.Equal(SaveRequestOutcome.Accepted, exporter.RequestSave(SaveMode.Mixed));
        Assert.Equal(SaveRequestOutcome.Busy, exporter.RequestSave(SaveMode.Mixed));

        _runner.Gate.SetResult();
        await exporter.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(3, completed);
        Assert.Equal(SaveRequestOutcome.Accepted, exporter.RequestSave(SaveMode.Mixed));
        await exporter.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task An_ffmpeg_failure_is_reported_and_temp_files_are_removed()
    {
        _video.Newest = () => 200_000;
        _video.Result = new VideoSnapshot([1], 70_000, 99_990, 1);
        _runner.ExitCode = 1;

        SaveResult result = await SaveAndWait(NewExporter(), SaveMode.Mixed);

        Assert.Equal("ffmpeg failed: boom", result.Error);
        Assert.Empty(Directory.GetDirectories(TempRoot));
        Assert.False(Directory.Exists(OutputRoot));
    }

    [Fact]
    public async Task Multitrack_saves_pass_multitrack_arguments()
    {
        _video.Newest = () => 200_000;
        _video.Result = new VideoSnapshot([1], 70_000, 99_990, 1);

        SaveResult result = await SaveAndWait(NewExporter(), SaveMode.Multitrack);

        Assert.Equal(SaveMode.Multitrack, result.Mode);
        Assert.True(ArgsAssert.HasPair(_runner.Calls.Single(), "-map", "2:a"));
    }

    [Fact]
    public void CleanTempRoot_removes_leftover_folders()
    {
        Directory.CreateDirectory(Path.Combine(TempRoot, "left-over"));
        File.WriteAllText(Path.Combine(TempRoot, "left-over", "video.ts"), "x");

        ClipExporter.CleanTempRoot(TempRoot);

        Assert.Empty(Directory.GetDirectories(TempRoot));
    }

    private sealed class FakeVideo : IVideoSource
    {
        public Func<long?> Newest { get; set; } = () => null;

        public VideoSnapshot? Result { get; set; }

        public List<(long EndQpc, TimeSpan Length)> Calls { get; } = [];

        public long? NewestCaptureQpc => Newest();

        public VideoSnapshot? Snapshot(long endQpc, TimeSpan length)
        {
            lock (Calls) Calls.Add((endQpc, length));
            return Result;
        }
    }

    private sealed class FakeAudio : IAudioSource
    {
        public List<(long From, long To)> Calls { get; } = [];

        public byte[] Extract(long fromQpc, long toQpc)
        {
            lock (Calls) Calls.Add((fromQpc, toQpc));
            return new byte[400];
        }
    }

    private sealed class FakeRunner : IFfmpegRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public List<bool> InputsExisted { get; } = [];

        public TaskCompletionSource? Gate { get; set; }

        public int ExitCode { get; set; }

        public async Task<FfmpegResult> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
        {
            string[] inputs = args.Select((arg, i) => (arg, i)).Where(x => x.arg == "-i").Select(x => args[x.i + 1]).ToArray();
            lock (Calls)
            {
                Calls.Add(args);
                InputsExisted.Add(inputs.All(File.Exists));
            }
            if (Gate is not null) await Gate.Task;
            if (ExitCode == 0) await File.WriteAllBytesAsync(args[^1], [1, 2, 3], cancellationToken);
            return new FfmpegResult(ExitCode, ExitCode == 0 ? [] : ["boom"]);
        }
    }
}
