namespace ClipThy.Tests.TestSupport;

public class TempDirTests
{
    [Fact]
    public void Creates_a_folder_and_deletes_it_on_dispose()
    {
        string path;
        using (var dir = new TempDir())
        {
            path = dir.Path;
            File.WriteAllText(dir.File("a.txt"), "x");
            Assert.True(File.Exists(Path.Combine(path, "a.txt")));
        }

        Assert.False(Directory.Exists(path));
    }
}
