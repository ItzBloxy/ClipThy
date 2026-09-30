using System.Diagnostics;
using ClipThy.Infrastructure;
using ClipThy.Tests.TestSupport;
using Microsoft.Win32;

namespace ClipThy.Tests.Infrastructure;

public class FileLogTests
{
    [Fact]
    public void Rolls_over_at_the_size_limit()
    {
        using var dir = new TempDir();
        var log = new FileLog(dir.File("clipthy.log"), maxBytes: 1000);

        for (int i = 0; i < 50; i++) log.Write($"line {i} {new string('x', 40)}");

        Assert.True(File.Exists(dir.File("clipthy.1.log")));
        Assert.True(new FileInfo(dir.File("clipthy.log")).Length <= 1000);
        Assert.Contains("line 49", File.ReadAllText(dir.File("clipthy.log")));
    }
}

public class AutostartTests
{
    [Fact]
    public void Enable_and_disable_round_trip()
    {
        const string testRoot = @"Software\ClipThy.Tests";
        string keyPath = $@"{testRoot}\{Guid.NewGuid():N}";
        var autostart = new Autostart(keyPath);
        try
        {
            Assert.False(autostart.IsEnabled);

            autostart.Enable(@"C:\Apps\ClipThy.exe");

            Assert.True(autostart.IsEnabled);
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(keyPath)!)
            {
                Assert.Equal("\"C:\\Apps\\ClipThy.exe\"", key.GetValue("ClipThy"));
            }

            autostart.Disable();

            Assert.False(autostart.IsEnabled);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(testRoot, throwOnMissingSubKey: false);
        }
    }
}

public class JobObjectTests
{
    [Fact]
    public void A_process_can_be_added_to_the_job()
    {
        var job = new JobObject();
        using Process process = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 5 127.0.0.1 > nul")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        try
        {
            job.Add(process);
        }
        finally
        {
            process.Kill();
        }
    }
}
