using ClipThy.Export;
using ClipThy.Tests.TestSupport;

namespace ClipThy.Tests.Export;

public class OutputPathsTests
{
    private static readonly DateTime PressTime = new(2026, 9, 30, 16, 45, 12);

    [Fact]
    public void Month_folder_is_year_dash_month() =>
        Assert.Equal(Path.Combine(@"C:\Root", "2026-09"), OutputPaths.MonthFolder(@"C:\Root", PressTime));

    [Fact]
    public void Reserve_creates_the_month_folder_and_names_the_clip()
    {
        using var dir = new TempDir();

        string path = OutputPaths.Reserve(dir.Path, PressTime);

        Assert.Equal(Path.Combine(dir.Path, "2026-09", "clip_2026-09-30_16-45-12.mp4"), path);
        Assert.True(Directory.Exists(Path.Combine(dir.Path, "2026-09")));
    }

    [Fact]
    public void Reserve_adds_a_number_when_the_name_is_taken()
    {
        using var dir = new TempDir();
        File.WriteAllText(OutputPaths.Reserve(dir.Path, PressTime), "x");
        string second = OutputPaths.Reserve(dir.Path, PressTime);
        File.WriteAllText(second, "x");

        string third = OutputPaths.Reserve(dir.Path, PressTime);

        Assert.EndsWith("clip_2026-09-30_16-45-12_2.mp4", second);
        Assert.EndsWith("clip_2026-09-30_16-45-12_3.mp4", third);
    }

    [Fact]
    public void A_new_month_gets_a_new_folder()
    {
        using var dir = new TempDir();

        string path = OutputPaths.Reserve(dir.Path, new DateTime(2026, 10, 1, 0, 0, 0));

        Assert.Equal(Path.Combine(dir.Path, "2026-10", "clip_2026-10-01_00-00-00.mp4"), path);
    }
}
