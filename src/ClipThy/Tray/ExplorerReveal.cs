using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClipThy.Tray;

/// <summary>
/// Shows files and folders in Explorer. A background tray app that just starts explorer.exe often gets its window
/// opened behind everything else, so this first lets any process take the foreground (ClipThy holds that right
/// right after the user clicks its notification or icon) and selects files with the shell's own
/// SHOpenFolderAndSelectItems. Call it on the UI (STA) thread.
/// </summary>
public static class ExplorerReveal
{
    private const int AsfwAny = -1;

    /// <summary>Opens the file's folder with the file selected. Returns false (with a reason) instead of throwing.</summary>
    public static bool TryReveal(string filePath, out string? error)
    {
        if (!File.Exists(filePath))
        {
            error = $"'{filePath}' no longer exists";
            return false;
        }

        IntPtr item = ILCreateFromPathW(Path.GetFullPath(filePath));
        if (item == IntPtr.Zero)
        {
            error = $"Windows couldn't resolve '{filePath}'";
            return false;
        }
        try
        {
            AllowSetForegroundWindow(AsfwAny);
            int result = SHOpenFolderAndSelectItems(item, 0, IntPtr.Zero, 0);
            error = result == 0 ? null : $"SHOpenFolderAndSelectItems failed (0x{result:X8})";
            return result == 0;
        }
        finally
        {
            ILFree(item);
        }
    }

    public static void OpenFolder(string folder)
    {
        AllowSetForegroundWindow(AsfwAny);
        Process.Start("explorer.exe", $"\"{folder}\"");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ILCreateFromPathW(string path);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr itemIdList);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr folder, uint count, IntPtr items, uint flags);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
