using System.Runtime.InteropServices;
using ClipThy.Export;

namespace ClipThy.Hotkeys;

/// <summary>Registers global hotkeys on a hidden message-only window and raises SaveRequested when pressed.</summary>
public sealed class HotkeyListener : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly Dictionary<int, SaveMode> _registered = [];
    private int _nextId = 1;

    public HotkeyListener() => CreateHandle(new CreateParams { Parent = HWND_MESSAGE });

    public event Action<SaveMode>? SaveRequested;

    /// <summary>Returns false when another program already owns the hotkey.</summary>
    public bool Register(Hotkey hotkey, SaveMode mode)
    {
        int id = _nextId++;
        if (!RegisterHotKey(Handle, id, (uint)hotkey.Modifiers | MOD_NOREPEAT, (uint)hotkey.Key)) return false;
        _registered[id] = mode;
        return true;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && _registered.TryGetValue((int)m.WParam, out SaveMode mode)) SaveRequested?.Invoke(mode);
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        foreach (int id in _registered.Keys) UnregisterHotKey(Handle, id);
        _registered.Clear();
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
