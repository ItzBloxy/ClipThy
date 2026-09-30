using System.Diagnostics;
using ClipThy.Audio;
using ClipThy.Timing;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ClipThy.Tests.Integration;

/// <summary>Needs real audio hardware: a microphone, and speakers (plays a quiet 1 s tone).</summary>
[Trait("Category", "Hardware")]
public class AudioCaptureTests
{
    [Fact]
    public async Task Microphone_packets_are_stamped_close_to_now()
    {
        var buffer = new AudioBuffer(Stopwatch.Frequency);
        var problems = new List<string>();
        using var capture = new AudioCapture(AudioSourceKind.Microphone, buffer, _ => { });
        capture.Problem += problem => problems.Add(problem);

        capture.Start();
        await Task.Delay(1500);

        Assert.Empty(problems);
        long? end = buffer.NewestEndQpc;
        Assert.NotNull(end);
        double lagMs = (Stopwatch.GetTimestamp() - end.Value) * 1000.0 / Stopwatch.Frequency;
        Assert.InRange(lagMs, -50, 250);
    }

    [Fact]
    public async Task System_audio_is_captured_with_current_timestamps()
    {
        var buffer = new AudioBuffer(Stopwatch.Frequency);
        using var capture = new AudioCapture(AudioSourceKind.SystemLoopback, buffer, _ => { });
        capture.Start();
        await Task.Delay(300);

        // Loopback only delivers packets while something plays, so play a quiet 1 s tone.
        using (var output = new WasapiOut())
        {
            output.Init(new SignalGenerator(48_000, 2) { Frequency = 440, Gain = 0.05 }.Take(TimeSpan.FromSeconds(1)));
            output.Play();
            await Task.Delay(1200);
        }

        long? end = buffer.NewestEndQpc;
        Assert.NotNull(end);
        double lagMs = (Stopwatch.GetTimestamp() - end.Value) * 1000.0 / Stopwatch.Frequency;
        Assert.InRange(lagMs, -50, 600);
        long now = Stopwatch.GetTimestamp();
        byte[] recent = buffer.Extract(now - QpcTime.FromSeconds(1.2, Stopwatch.Frequency), now);
        Assert.Contains(recent, b => b != 0);
    }
}
