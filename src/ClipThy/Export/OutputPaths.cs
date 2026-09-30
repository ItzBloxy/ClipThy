using System.Globalization;

namespace ClipThy.Export;

/// <summary>Builds ShareX-style paths: root\yyyy-MM\clip_yyyy-MM-dd_HH-mm-ss.mp4.</summary>
public static class OutputPaths
{
    public static string MonthFolder(string root, DateTime localTime) =>
        Path.Combine(root, localTime.ToString("yyyy-MM", CultureInfo.InvariantCulture));

    /// <summary>Creates the month folder and returns a clip path that doesn't exist yet.</summary>
    public static string Reserve(string root, DateTime localTime)
    {
        string folder = MonthFolder(root, localTime);
        Directory.CreateDirectory(folder);
        string baseName = "clip_" + localTime.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        string path = Path.Combine(folder, baseName + ".mp4");
        for (int n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(folder, $"{baseName}_{n}.mp4");
        }
        return path;
    }
}
