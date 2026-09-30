using ClipThy.Config;
using ClipThy.Tests.TestSupport;

namespace ClipThy.Tests.Config;

public class SettingsStoreTests
{
    [Fact]
    public void Missing_file_is_created_with_defaults()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("config.json"));

        SettingsLoadResult result = store.Load();

        Assert.True(result.CreatedNew);
        Assert.Empty(result.Warnings);
        Assert.Equal(new AppSettings(), result.Settings);
        Assert.Contains("\"clipSeconds\": 30", File.ReadAllText(store.FilePath));
    }

    [Fact]
    public void Missing_properties_keep_their_defaults()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("config.json"), """{ "clipSeconds": 60, "playSound": false }""");

        SettingsLoadResult result = new SettingsStore(dir.File("config.json")).Load();

        Assert.False(result.CreatedNew);
        Assert.Empty(result.Warnings);
        Assert.Equal(new AppSettings { ClipSeconds = 60, PlaySound = false }, result.Settings);
    }

    [Fact]
    public void Invalid_values_fall_back_to_defaults_with_a_warning_each()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("config.json"),
            """{ "clipSeconds": 500, "fps": 25, "encoder": "x264", "hotkeyMixed": "Alt+Nope", "videoBitrateMbps": 1 }""");

        SettingsLoadResult result = new SettingsStore(dir.File("config.json")).Load();

        Assert.Equal(new AppSettings(), result.Settings);
        Assert.Equal(5, result.Warnings.Count);
    }

    [Fact]
    public void Encoder_is_normalised_to_lower_case()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("config.json"), """{ "encoder": " NVENC " }""");

        Assert.Equal("nvenc", new SettingsStore(dir.File("config.json")).Load().Settings.Encoder);
    }

    [Fact]
    public void Broken_json_is_kept_aside_and_defaults_are_used()
    {
        using var dir = new TempDir();
        string path = dir.File("config.json");
        File.WriteAllText(path, "{ not json");

        SettingsLoadResult result = new SettingsStore(path).Load();

        Assert.Equal(new AppSettings(), result.Settings);
        Assert.Single(result.Warnings);
        Assert.Equal("{ not json", File.ReadAllText(path + ".bad"));
        Assert.Contains("\"clipSeconds\": 30", File.ReadAllText(path));
    }

    [Fact]
    public void Saved_settings_load_back_unchanged()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("config.json"));
        var settings = new AppSettings { ClipSeconds = 120, Encoder = "mf", OutputRoot = @"D:\Clips", PlaySound = false };

        store.Save(settings);

        Assert.Equal(settings, store.Load().Settings);
    }
}
