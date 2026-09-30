using System.Diagnostics;

namespace ClipThy.Export;

public sealed record FfmpegResult(int ExitCode, IReadOnlyList<string> StdErrTail);

public interface IFfmpegRunner
{
    Task<FfmpegResult> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default);
}

/// <summary>Runs a short-lived ffmpeg (the export) and collects the end of its stderr.</summary>
public sealed class ProcessFfmpegRunner(Func<string> ffmpegPath, Action<Process>? onStarted = null) : IFfmpegRunner
{
    private const int TailLines = 20;

    public async Task<FfmpegResult> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(ffmpegPath())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (string arg in args) startInfo.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = startInfo };
        var tail = new Queue<string>();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (tail)
            {
                tail.Enqueue(e.Data);
                if (tail.Count > TailLines) tail.Dequeue();
            }
        };
        process.OutputDataReceived += (_, _) => { };

        process.Start();
        onStarted?.Invoke(process);
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        await process.WaitForExitAsync(cancellationToken);

        lock (tail) return new FfmpegResult(process.ExitCode, tail.ToArray());
    }
}
