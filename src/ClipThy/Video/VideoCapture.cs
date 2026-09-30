using System.ComponentModel;
using System.Diagnostics;
using ClipThy.Infrastructure;
using ClipThy.Timing;

namespace ClipThy.Video;

public enum CaptureState
{
    Starting,
    Recording,
    Restarting,
    Paused,
    Failed,
}

public sealed record CaptureStatus(CaptureState State, EncoderKind Encoder, string? Detail = null);

/// <summary>
/// Runs the capture ffmpeg, feeds its stdout into <see cref="VideoBuffer"/>, and keeps it alive: restarts on
/// exit or stall with backoff, restarts on request (resume, display change), pauses while the PC is locked,
/// and falls back from NVENC to Media Foundation when NVENC can't start.
/// </summary>
public sealed class VideoCapture : IDisposable
{
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HealthyRun = TimeSpan.FromSeconds(60);
    private const int NotifyAfterFailures = 5;
    private const int StdErrTailLines = 50;

    private readonly VideoBuffer _buffer;
    private readonly Func<EncoderKind, IReadOnlyList<string>> _buildArgs;
    private readonly string _ffmpegPath;
    private readonly EncoderMode _mode;
    private readonly JobObject? _job;
    private readonly Action<string> _log;
    private readonly object _lock = new();
    private readonly ManualResetEventSlim _wake = new(false);
    private Thread? _thread;
    private Process? _process;
    private bool _disposed;
    private bool _paused;
    private bool _restartRequested;
    private EncoderKind _encoder;
    private long _lastPacketQpc;

    private sealed record RunResult(bool ProducedPackets, IReadOnlyList<string> StdErrTail, TimeSpan Duration);

    public VideoCapture(
        VideoBuffer buffer,
        Func<EncoderKind, IReadOnlyList<string>> buildArgs,
        string ffmpegPath,
        EncoderMode mode,
        JobObject? job,
        Action<string> log)
    {
        _buffer = buffer;
        _buildArgs = buildArgs;
        _ffmpegPath = ffmpegPath;
        _mode = mode;
        _job = job;
        _log = log;
        _encoder = EncoderPolicy.Initial(mode);
    }

    /// <summary>Raised on the capture thread whenever the state changes.</summary>
    public event Action<CaptureStatus>? StatusChanged;

    /// <summary>Raised once when NVENC couldn't start and the session switched to Media Foundation.</summary>
    public event Action<string>? EncoderFellBack;

    /// <summary>Raised once per streak of failures, after 5 failed starts in a row.</summary>
    public event Action<string>? PersistentFailure;

    public EncoderKind Encoder
    {
        get { lock (_lock) return _encoder; }
    }

    public int? CurrentProcessId
    {
        get
        {
            lock (_lock)
            {
                try { return _process?.Id; }
                catch (InvalidOperationException) { return null; }
            }
        }
    }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "ClipThy video capture" };
        _thread.Start();
    }

    public void RequestRestart(string reason)
    {
        _log($"Video capture restart requested: {reason}");
        lock (_lock)
        {
            _restartRequested = true;
            KillCurrent();
        }
        _wake.Set();
    }

    public void Pause()
    {
        lock (_lock)
        {
            _paused = true;
            KillCurrent();
        }
        _wake.Set();
    }

    public void Resume()
    {
        lock (_lock)
        {
            _paused = false;
            _restartRequested = true;
        }
        _wake.Set();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            KillCurrent();
        }
        _wake.Set();
        _thread?.Join(TimeSpan.FromSeconds(5));
    }

    private void Run()
    {
        int failures = 0;
        bool notified = false;
        while (true)
        {
            _wake.Reset();
            bool paused;
            lock (_lock)
            {
                if (_disposed) return;
                paused = _paused;
                _restartRequested = false;
            }
            if (paused)
            {
                Raise(CaptureState.Paused);
                _wake.Wait();
                continue;
            }

            Raise(failures == 0 ? CaptureState.Starting : CaptureState.Restarting);
            RunResult result;
            try
            {
                result = RunOnce();
            }
            catch (Exception ex)
            {
                // A bug must not kill the capture thread (and with it the app): log it and retry with backoff.
                _log($"Video capture error: {ex}");
                lock (_lock)
                {
                    KillCurrent();
                    _process = null;
                }
                result = new RunResult(false, [ex.Message], TimeSpan.Zero);
            }

            bool intentional;
            lock (_lock)
            {
                if (_disposed) return;
                intentional = _restartRequested || _paused;
            }
            if (intentional)
            {
                failures = 0;
                continue;
            }

            if (result.Duration >= HealthyRun)
            {
                failures = 0;
                notified = false;
            }
            failures++;

            EncoderKind current = Encoder;
            if (EncoderPolicy.ShouldFallBack(_mode, current, result.ProducedPackets, result.StdErrTail))
            {
                lock (_lock) _encoder = EncoderKind.MediaFoundation;
                _log("NVENC could not start; switching to Media Foundation for this session.");
                EncoderFellBack?.Invoke(
                    "NVENC couldn't start, so ClipThy is using Windows Media Foundation on the same GPU. Updating the NVIDIA driver usually fixes this.");
                failures = 0;
                continue;
            }

            string lastLine = result.StdErrTail.LastOrDefault() ?? "ffmpeg stopped unexpectedly";
            if (failures >= NotifyAfterFailures && !notified)
            {
                notified = true;
                Raise(CaptureState.Failed, lastLine);
                PersistentFailure?.Invoke(lastLine);
            }
            else
            {
                Raise(CaptureState.Restarting, lastLine);
            }
            _wake.Wait(EncoderPolicy.Backoff(failures));
        }
    }

    private RunResult RunOnce()
    {
        EncoderKind encoder = Encoder;
        var startInfo = new ProcessStartInfo(_ffmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in _buildArgs(encoder)) startInfo.ArgumentList.Add(arg);

        var process = new Process { StartInfo = startInfo };
        var tail = new Queue<string>();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (tail)
            {
                tail.Enqueue(e.Data);
                if (tail.Count > StdErrTailLines) tail.Dequeue();
            }
        };

        long started = Stopwatch.GetTimestamp();
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            process.Dispose();
            return new RunResult(false, [$"Could not start ffmpeg ('{_ffmpegPath}'): {ex.Message}"], TimeSpan.Zero);
        }

        try { _job?.Add(process); }
        catch (Win32Exception ex) { _log($"Could not add ffmpeg to the job object: {ex.Message}"); }

        _buffer.Clear();
        lock (_lock)
        {
            _process = process;
            if (_disposed || _paused || _restartRequested) KillCurrent();
        }
        process.BeginErrorReadLine();
        _log($"Capture ffmpeg started (pid {process.Id}, {encoder}).");

        bool produced = false;
        Interlocked.Exchange(ref _lastPacketQpc, Stopwatch.GetTimestamp());
        using (new System.Threading.Timer(_ => KillIfStalled(process), null, 500, 500))
        {
            Stream stdout = process.StandardOutput.BaseStream;
            byte[] chunk = new byte[64 * 1024];
            try
            {
                int read;
                while ((read = stdout.Read(chunk, 0, chunk.Length)) > 0)
                {
                    long arrivalQpc = Stopwatch.GetTimestamp();
                    long arrivalUtc90k = MpegTs.UtcTo90k(DateTime.UtcNow);
                    _buffer.Append(chunk.AsSpan(0, read), arrivalQpc, arrivalUtc90k);
                    Interlocked.Exchange(ref _lastPacketQpc, arrivalQpc);
                    if (!produced)
                    {
                        produced = true;
                        Raise(CaptureState.Recording);
                    }
                }
            }
            catch (IOException) { }
        }

        process.WaitForExit();
        lock (_lock) _process = null;
        string[] lines;
        lock (tail) lines = tail.ToArray();
        TimeSpan duration = Stopwatch.GetElapsedTime(started);
        _log($"Capture ffmpeg exited (code {process.ExitCode}) after {duration.TotalSeconds:F1} s; timestamp warnings so far: {_buffer.TimestampWarnings}. {string.Join(" | ", lines.TakeLast(3))}");
        process.Dispose();
        return new RunResult(produced, lines, duration);
    }

    private void KillIfStalled(Process process)
    {
        long silentFor = Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastPacketQpc);
        if (silentFor < QpcTime.FromTimeSpan(StallTimeout, Stopwatch.Frequency)) return;
        _log("No video for 3 s; restarting ffmpeg.");
        Interlocked.Exchange(ref _lastPacketQpc, Stopwatch.GetTimestamp());
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }

    private void KillCurrent()
    {
        try { _process?.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }

    private void Raise(CaptureState state, string? detail = null) =>
        StatusChanged?.Invoke(new CaptureStatus(state, Encoder, detail));
}
