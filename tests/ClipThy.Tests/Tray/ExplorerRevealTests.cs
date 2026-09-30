using ClipThy.Tray;

namespace ClipThy.Tests.Tray;

public class ExplorerRevealTests
{
    [Fact]
    public void A_missing_file_is_reported_instead_of_opening_explorer()
    {
        string missing = Path.Combine(Path.GetTempPath(), "ClipThyTests", Guid.NewGuid().ToString("N") + ".mp4");

        Assert.False(ExplorerReveal.TryReveal(missing, out string? error));
        Assert.Contains(missing, error);
    }
}
