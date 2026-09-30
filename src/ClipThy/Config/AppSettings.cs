namespace ClipThy.Config;

/// <summary>Contents of %APPDATA%\ClipThy\config.json. Missing properties keep these defaults.</summary>
public sealed record AppSettings
{
    public static readonly int[] ClipLengthPresets = [15, 30, 60, 120];

    public int ClipSeconds { get; init; } = 30;
    public string HotkeyMixed { get; init; } = "Alt+F10";
    public string HotkeyMultitrack { get; init; } = "Alt+F11";
    public string OutputRoot { get; init; } = DefaultOutputRoot;
    public string Encoder { get; init; } = "auto";
    public int Fps { get; init; } = 60;
    public int VideoBitrateMbps { get; init; } = 20;
    public bool PlaySound { get; init; } = true;
    public string FfmpegPath { get; init; } = "ffmpeg";

    public static string DefaultOutputRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ShareX", "Screenshots");
}
