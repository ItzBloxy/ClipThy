using Microsoft.Win32;

namespace ClipThy.Infrastructure;

/// <summary>The HKCU Run entry that starts ClipThy at login.</summary>
public sealed class Autostart(string keyPath = Autostart.RunKeyPath, string valueName = "ClipThy")
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public bool IsEnabled
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue(valueName) is string;
        }
    }

    public void Enable(string exePath)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath);
        key.SetValue(valueName, $"\"{exePath}\"");
    }

    public void Disable()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}
