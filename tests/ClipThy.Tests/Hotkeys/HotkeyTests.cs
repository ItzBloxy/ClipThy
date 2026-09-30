using System.Windows.Forms;
using ClipThy.Hotkeys;

namespace ClipThy.Tests.Hotkeys;

public class HotkeyTests
{
    [Theory]
    [InlineData("Alt+F10", HotkeyModifiers.Alt, Keys.F10, "Alt+F10")]
    [InlineData("ctrl+shift+s", HotkeyModifiers.Control | HotkeyModifiers.Shift, Keys.S, "Ctrl+Shift+S")]
    [InlineData(" Alt + F11 ", HotkeyModifiers.Alt, Keys.F11, "Alt+F11")]
    [InlineData("F9", HotkeyModifiers.None, Keys.F9, "F9")]
    [InlineData("Win+Alt+1", HotkeyModifiers.Win | HotkeyModifiers.Alt, Keys.D1, "Alt+Win+D1")]
    public void Parses_valid_hotkeys(string text, HotkeyModifiers modifiers, Keys key, string display)
    {
        Assert.True(Hotkey.TryParse(text, out Hotkey hotkey));
        Assert.Equal(modifiers, hotkey.Modifiers);
        Assert.Equal(key, hotkey.Key);
        Assert.Equal(display, hotkey.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Alt+")]
    [InlineData("+F10")]
    [InlineData("Alt+Alt+F10")]
    [InlineData("Hyper+F1")]
    [InlineData("Alt+Nope")]
    [InlineData("Alt+10")]
    [InlineData("Alt+Shift")]
    [InlineData("Alt+ShiftKey")]
    [InlineData("Alt+F10,A")]
    public void Rejects_invalid_hotkeys(string? text) => Assert.False(Hotkey.TryParse(text, out _));
}
