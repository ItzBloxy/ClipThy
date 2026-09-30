using ClipThy.Audio;
using ClipThy.Config;
using ClipThy.Timing;
using ClipThy.Video;

namespace ClipThy.Export;

public enum SaveRequestOutcome
{
    Accepted,
    Busy,
}

public sealed record SaveResult(SaveMode Mode, string? Path, double LengthSeconds, string? Error)
{
    public bool Success => Error is null;
}

/// <summary>
/// Turns a hotkey press into a file. Stage 1 (per press, right away): wait until the video buffer has reached
/// the press time, then copy the video and both audio spans. Stage 2 (serialized): write temp files, run the
/// export ffmpeg, move the MP4 into the month folder.
/// </summary>
public sealed class ClipExporter
{
    public const int MaxPending = 3;
    public const string NothingToSave = "Nothing to save yet";

    private static readonly TimeSpan MinWait = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);

    private readonly IVideoSource _video;
    private readonly IAudioSource _system;
    private readonly IAudioSource _mic;
    private readonly IFfmpegRunner _ffmpeg;
    private readonly IClock _clock;
    private readonly Func<AppSettings> _settings;
    private readonly string _tempRoot;
    private readonly Func<TimeSpan, Task> _delay;
    private readonly SemaphoreSlim _exportLock = new(1, 1);
    private readonly List<Task> _running = [];
    private int _pending;

    private sealed record ClipData(VideoSnapshot Video, byte[] SystemAudio, byte[] MicAudio, double LengthSeconds);

    public ClipExporter(
        IVideoSource video,
        IAudioSource system,
        IAudioSource mic,
        IFfmpegRunner ffmpeg,
        IClock clock,
        Func<AppSettings> settings,
        string tempRoot,
        Func<TimeSpan, Task>? delay = null)
    {
        _video = video;
        _system = system;
        _mic = mic;
        _ffmpeg = ffmpeg;
        _clock = clock;
        _settings = settings;
        _tempRoot = tempRoot;
        _delay = delay ?? (d => Task.Delay(d));
    }

    /// <summary>Raised on a background thread when a save finishes (successfully or not).</summary>
    public event Action<SaveResult>? Completed;

    public int Pending => Volatile.Read(ref _pending);

    public SaveRequestOutcome RequestSave(SaveMode mode)
    {
        if (Interlocked.Increment(ref _pending) > MaxPending)
        {
            Interlocked.Decrement(ref _pending);
            return SaveRequestOutcome.Busy;
        }

        long pressQpc = _clock.QpcNow;
        DateTime pressLocal = _clock.UtcNow.ToLocalTime();
        AppSettings settings = _settings();
        Task task = Task.Run(() => SaveAsync(mode, pressQpc, pressLocal, settings))
            .ContinueWith(_ => ReleaseMemoryWhenIdle(), TaskScheduler.Default);
        lock (_running)
        {
            _running.RemoveAll(t => t.IsCompleted);
            _running.Add(task);
        }
        return SaveRequestOutcome.Accepted;
    }

    /// <summary>Completes when every accepted save has finished.</summary>
    public Task WhenIdleAsync()
    {
        lock (_running) return Task.WhenAll(_running.ToArray());
    }

    /// <summary>
    /// A save copies tens of MB into large arrays and then drops them. Workstation GC can keep that memory committed
    /// for a long time (the tray app sat at ~250 MB after one save), so once the last pending save has finished,
    /// run one full collection that hands the memory back to Windows. Live data is only ~20 MB, so it takes milliseconds.
    /// </summary>
    private void ReleaseMemoryWhenIdle()
    {
        if (Pending > 0) return;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    /// <summary>Deletes temp folders left behind by a previous run (for example after a crash).</summary>
    public static void CleanTempRoot(string tempRoot)
    {
        if (!Directory.Exists(tempRoot)) return;
        foreach (string folder in Directory.GetDirectories(tempRoot))
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private async Task SaveAsync(SaveMode mode, long pressQpc, DateTime pressLocal, AppSettings settings)
    {
        SaveResult result;
        try
        {
            ClipData? clip = await CaptureAsync(pressQpc, settings);
            result = clip is null
                ? new SaveResult(mode, null, 0, NothingToSave)
                : await ExportAsync(mode, clip, pressLocal, settings);
        }
        catch (Exception ex)
        {
            result = new SaveResult(mode, null, 0, ex.Message);
        }
        finally
        {
            Interlocked.Decrement(ref _pending);
        }
        Completed?.Invoke(result);
    }

    private async Task<ClipData?> CaptureAsync(long pressQpc, AppSettings settings)
    {
        long frequency = _clock.Frequency;
        long minWait = QpcTime.FromTimeSpan(MinWait, frequency);
        long maxWait = QpcTime.FromTimeSpan(MaxWait, frequency);
        long waitStart = _clock.QpcNow;
        while (true)
        {
            long waited = _clock.QpcNow - waitStart;
            bool videoCaughtUp = _video.NewestCaptureQpc is long newest && newest >= pressQpc;
            if ((waited >= minWait && videoCaughtUp) || waited >= maxWait) break;
            await _delay(PollInterval);
        }

        VideoSnapshot? video = _video.Snapshot(pressQpc, TimeSpan.FromSeconds(settings.ClipSeconds));
        if (video is null) return null;

        long audioEnd = video.LastFrameQpc + QpcTime.FromSeconds(1.0 / settings.Fps, frequency);
        return new ClipData(
            video,
            _system.Extract(video.FirstFrameQpc, audioEnd),
            _mic.Extract(video.FirstFrameQpc, audioEnd),
            QpcTime.ToSeconds(audioEnd - video.FirstFrameQpc, frequency));
    }

    private async Task<SaveResult> ExportAsync(SaveMode mode, ClipData clip, DateTime pressLocal, AppSettings settings)
    {
        await _exportLock.WaitAsync();
        string folder = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            string videoTs = Path.Combine(folder, "video.ts");
            string systemWav = Path.Combine(folder, "system.wav");
            string micWav = Path.Combine(folder, "mic.wav");
            string mp4 = Path.Combine(folder, "clip.mp4");

            await File.WriteAllBytesAsync(videoTs, clip.Video.Data);
            WavWriter.Write(systemWav, clip.SystemAudio);
            WavWriter.Write(micWav, clip.MicAudio);

            FfmpegResult run = await _ffmpeg.RunAsync(ExportArgs.Build(mode, videoTs, systemWav, micWav, mp4));
            if (run.ExitCode != 0 || !File.Exists(mp4))
            {
                string reason = run.StdErrTail.LastOrDefault() ?? $"exit code {run.ExitCode}";
                return new SaveResult(mode, null, 0, $"ffmpeg failed: {reason}");
            }

            string destination = OutputPaths.Reserve(settings.OutputRoot, pressLocal);
            File.Move(mp4, destination);
            return new SaveResult(mode, destination, clip.LengthSeconds, null);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            _exportLock.Release();
        }
    }
}
