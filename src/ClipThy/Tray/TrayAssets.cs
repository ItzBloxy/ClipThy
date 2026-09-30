using System.Drawing.Drawing2D;
using System.Media;
using System.Runtime.InteropServices;
using ClipThy.Export;

namespace ClipThy.Tray;

/// <summary>Tray icons and the save sound, generated in code so there are no asset files.</summary>
internal static class TrayAssets
{
    public static Icon Dot(Color color)
    {
        using var bitmap = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 4, 4, 24, 24);
        }
        IntPtr handle = bitmap.GetHicon();
        try
        {
            using Icon temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    /// <summary>An 80 ms two-note chime (48 kHz mono) with short fades so it doesn't click.</summary>
    public static SoundPlayer SaveSound()
    {
        const int rate = 48_000;
        int frames = rate * 80 / 1000;
        var pcm = new byte[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            double t = (double)i / rate;
            double frequency = i < frames / 2 ? 880 : 1320;
            double fade = Math.Min(1.0, Math.Min(i, frames - i) / (rate * 0.005));
            short sample = (short)(Math.Sin(2 * Math.PI * frequency * t) * fade * 0.3 * short.MaxValue);
            BitConverter.TryWriteBytes(pcm.AsSpan(i * 2), sample);
        }
        var stream = new MemoryStream();
        WavWriter.Write(stream, pcm, rate, channels: 1, bitsPerSample: 16);
        stream.Position = 0;
        var player = new SoundPlayer(stream);
        player.Load();
        return player;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
