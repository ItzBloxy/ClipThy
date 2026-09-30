using System.Diagnostics;
using ClipThy.Infrastructure;

namespace ClipThy.Tests.Infrastructure;

public class ProcessTimerResolutionTests
{
    private static Process StartIdleChild() => Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 > nul")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
    })!;

    [Fact]
    public void A_running_child_process_gets_the_fine_timer()
    {
        using Process process = StartIdleChild();
        try
        {
            Assert.True(ProcessTimerResolution.TryRequest(process, 1, out string? error), error);
            Assert.Null(error);
        }
        finally
        {
            process.Kill();
        }
    }

    [Fact]
    public void An_exited_process_reports_an_error_instead_of_throwing()
    {
        using Process process = StartIdleChild();
        process.Kill();
        process.WaitForExit();

        Assert.False(ProcessTimerResolution.TryRequest(process, 1, out string? error));
        Assert.False(string.IsNullOrEmpty(error));
    }
}
