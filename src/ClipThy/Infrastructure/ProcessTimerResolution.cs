using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClipThy.Infrastructure;

/// <summary>
/// Makes another process request a finer Windows timer. Since Windows 10 2004 timer resolution is per-process, so
/// ClipThy calling timeBeginPeriod itself doesn't help its child ffmpeg. Instead this starts one thread inside the
/// child that calls kernel32!timeBeginPeriod. kernel32 is mapped at the same address in every process, so nothing is
/// written into the child, and the request ends automatically when the child exits.
/// Why: ffmpeg's ddagrab paces capture with Sleep(); on the default 15.6 ms timer it skipped ~4–5 % of 60 fps frames.
/// </summary>
public static class ProcessTimerResolution
{
    private const uint TimerNoError = 0;
    private const uint WaitObject0 = 0;

    /// <summary>Returns false (with a reason) instead of throwing, so callers can log and carry on.</summary>
    public static bool TryRequest(Process process, uint milliseconds, out string? error)
    {
        try
        {
            IntPtr timeBeginPeriod = GetProcAddress(GetModuleHandle("kernel32.dll"), "timeBeginPeriod");
            if (timeBeginPeriod == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());

            IntPtr thread = CreateRemoteThread(process.Handle, IntPtr.Zero, UIntPtr.Zero, timeBeginPeriod, new IntPtr(milliseconds), 0, out _);
            if (thread == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (WaitForSingleObject(thread, 5000) != WaitObject0)
                {
                    error = "the timer request did not finish within 5 s";
                    return false;
                }
                GetExitCodeThread(thread, out uint result);
                if (result != TimerNoError)
                {
                    error = $"timeBeginPeriod returned {result}";
                    return false;
                }
            }
            finally
            {
                CloseHandle(thread);
            }

            error = null;
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            error = ex.Message;
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr attributes, UIntPtr stackSize, IntPtr startAddress, IntPtr parameter, uint flags, out uint threadId);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    private static extern bool GetExitCodeThread(IntPtr thread, out uint exitCode);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string procName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string moduleName);
}
