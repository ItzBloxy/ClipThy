using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ClipThy.Tray;

/// <summary>
/// "ClipThy.exe --sync-test": an 800×800 window on the primary monitor that flashes white and beeps at the
/// same moment once per second. Record a clip while it runs and analyse it with tools\sync-check.ps1.
/// </summary>
public sealed class SyncTestForm : Form
{
    private readonly WasapiOut _output = new(AudioClientShareMode.Shared, useEventSync: true, latency: 20);

    public SyncTestForm()
    {
        Text = "ClipThy sync test – close this window to stop";
        BackColor = Color.Black;
        StartPosition = FormStartPosition.Manual;
        Rectangle screen = Screen.PrimaryScreen!.Bounds;
        Bounds = new Rectangle(screen.X + (screen.Width - 800) / 2, screen.Y + (screen.Height - 800) / 2, 800, 800);
        TopMost = true;

        var beeps = new BeepProvider();
        beeps.BeepStarted += () => BeginInvoke(Flash);
        _output.Init(beeps);
        Shown += (_, _) => _output.Play();
        FormClosed += (_, _) => _output.Dispose();
    }

    private async void Flash()
    {
        BackColor = Color.White;
        await Task.Delay(100);
        BackColor = Color.Black;
    }

    /// <summary>48 kHz stereo silence with a 50 ms 1 kHz beep at the start of every second.</summary>
    private sealed class BeepProvider : ISampleProvider
    {
        private const int Rate = 48_000;
        private long _frame;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);

        public event Action? BeepStarted;

        public int Read(float[] buffer, int offset, int count)
        {
            for (int i = 0; i < count / 2; i++, _frame++)
            {
                long n = _frame % Rate;
                if (n == 0) BeepStarted?.Invoke();
                float sample = n < Rate / 20 ? 0.5f * MathF.Sin(2 * MathF.PI * 1000 * n / Rate) : 0f;
                buffer[offset + 2 * i] = sample;
                buffer[offset + 2 * i + 1] = sample;
            }
            return count;
        }
    }
}
