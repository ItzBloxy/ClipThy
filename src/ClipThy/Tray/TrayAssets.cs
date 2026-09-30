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

    /// <summary>The soft "clip saved" chime (see <see cref="Chime"/>), loaded and ready to play.</summary>
    public static SoundPlayer SaveSound()
    {
        var stream = new MemoryStream();
        WavWriter.Write(stream, Chime.ClipSaved(), Chime.SampleRate, channels: 1, bitsPerSample: 16);
        stream.Position = 0;
        var player = new SoundPlayer(stream);
        player.Load();
        return player;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
