using ClipThy.Infrastructure;
using ClipThy.Tray;

namespace ClipThy;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            new FileLog(FileLog.DefaultPath).Write($"Unhandled exception: {e.ExceptionObject}");
        ApplicationConfiguration.Initialize();

        if (args.Contains("--sync-test", StringComparer.OrdinalIgnoreCase))
        {
            Application.Run(new SyncTestForm());
            return;
        }

        using var singleInstance = new Mutex(initiallyOwned: true, @"Local\ClipThy", out bool isFirstInstance);
        if (!isFirstInstance) return;
        Application.Run(new TrayApp());
    }
}
