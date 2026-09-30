using System.Text.Json;
using ClipThy.Hotkeys;

namespace ClipThy.Config;

public sealed record SettingsLoadResult(AppSettings Settings, bool CreatedNew, IReadOnlyList<string> Warnings);

/// <summary>Loads, validates and saves config.json.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public SettingsStore(string filePath) => FilePath = filePath;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipThy", "config.json");

    public string FilePath { get; }

    public SettingsLoadResult Load()
    {
        if (!File.Exists(FilePath))
        {
            var defaults = new AppSettings();
            Save(defaults);
            return new SettingsLoadResult(defaults, CreatedNew: true, Warnings: []);
        }

        AppSettings? loaded;
        try
        {
            loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json);
        }
        catch (JsonException ex)
        {
            File.Copy(FilePath, FilePath + ".bad", overwrite: true);
            var defaults = new AppSettings();
            Save(defaults);
            return new SettingsLoadResult(defaults, CreatedNew: false,
                [$"config.json is not valid JSON ({ex.Message}). Using defaults; the broken file was kept as config.json.bad."]);
        }

        var (settings, warnings) = Validate(loaded ?? new AppSettings());
        return new SettingsLoadResult(settings, CreatedNew: false, warnings);
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Json));
    }

    public static (AppSettings Settings, IReadOnlyList<string> Warnings) Validate(AppSettings s)
    {
        var defaults = new AppSettings();
        var warnings = new List<string>();

        T Check<T>(string name, T value, bool valid, T fallback)
        {
            if (valid) return value;
            warnings.Add($"{name} value '{value}' is invalid; using '{fallback}'.");
            return fallback;
        }

        string encoder = (s.Encoder ?? "").Trim().ToLowerInvariant();
        var settings = new AppSettings
        {
            ClipSeconds = Check("clipSeconds", s.ClipSeconds, s.ClipSeconds is >= 5 and <= 120, defaults.ClipSeconds),
            HotkeyMixed = Check("hotkeyMixed", s.HotkeyMixed, Hotkey.TryParse(s.HotkeyMixed, out _), defaults.HotkeyMixed),
            HotkeyMultitrack = Check("hotkeyMultitrack", s.HotkeyMultitrack, Hotkey.TryParse(s.HotkeyMultitrack, out _), defaults.HotkeyMultitrack),
            OutputRoot = Check("outputRoot", s.OutputRoot, !string.IsNullOrWhiteSpace(s.OutputRoot), defaults.OutputRoot),
            Encoder = Check("encoder", encoder, encoder is "auto" or "nvenc" or "mf", defaults.Encoder),
            Fps = Check("fps", s.Fps, s.Fps is 30 or 60, defaults.Fps),
            VideoBitrateMbps = Check("videoBitrateMbps", s.VideoBitrateMbps, s.VideoBitrateMbps is >= 2 and <= 100, defaults.VideoBitrateMbps),
            PlaySound = s.PlaySound,
            FfmpegPath = Check("ffmpegPath", s.FfmpegPath, !string.IsNullOrWhiteSpace(s.FfmpegPath), defaults.FfmpegPath),
        };
        return (settings, warnings);
    }
}
