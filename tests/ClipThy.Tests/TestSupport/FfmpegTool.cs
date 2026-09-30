using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ClipThy.Tests.TestSupport;

/// <summary>Runs ffmpeg/ffprobe from PATH for integration tests.</summary>
public static class FfmpegTool
{
    public static string Run(string exe, params string[] args)
    {
        var startInfo = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in args) startInfo.ArgumentList.Add(arg);
        using Process process = Process.Start(startInfo)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"{exe} exited with {process.ExitCode}: {stderr.Result}");
        return stdout.Result;
    }

    public static JsonElement Probe(string file) => JsonDocument.Parse(Run("ffprobe",
        "-v", "error",
        "-show_entries", "format=duration:stream=index,codec_type,codec_name,color_space,color_primaries,color_transfer:stream_tags=handler_name:stream_disposition=default",
        "-of", "json", file)).RootElement;

    /// <summary>Top-level MP4 box types in file order (to check that moov comes before mdat).</summary>
    public static List<string> TopLevelBoxes(string mp4)
    {
        var boxes = new List<string>();
        using FileStream file = File.OpenRead(mp4);
        var header = new byte[16];
        while (file.Position < file.Length)
        {
            file.ReadExactly(header, 0, 8);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            boxes.Add(Encoding.ASCII.GetString(header, 4, 4));
            if (size == 0) break; // box runs to the end of the file
            if (size == 1)
            {
                file.ReadExactly(header, 8, 8);
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
                file.Seek(size - 16, SeekOrigin.Current);
            }
            else
            {
                file.Seek(size - 8, SeekOrigin.Current);
            }
        }
        return boxes;
    }
}
