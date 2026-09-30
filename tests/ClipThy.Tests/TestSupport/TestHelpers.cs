using System.Diagnostics;

namespace ClipThy.Tests.TestSupport;

/// <summary>A unique temp folder, deleted on dispose.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ClipThyTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
    }
}

public static class ArgsAssert
{
    /// <summary>True when <paramref name="key"/> is immediately followed by <paramref name="value"/>.</summary>
    public static bool HasPair(IReadOnlyList<string> args, string key, string value) =>
        Enumerable.Range(0, args.Count - 1).Any(i => args[i] == key && args[i + 1] == value);
}

public static class Wait
{
    public static async Task Until(Func<bool> condition, TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > timeout) throw new TimeoutException("Condition was not met in time.");
            await Task.Delay(50);
        }
    }
}
