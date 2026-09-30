using System.Windows.Forms;
using ClipThy.Export;
using ClipThy.Hotkeys;

namespace ClipThy.Tests.Hotkeys;

public class HotkeyListenerTests
{
    [Fact]
    public void A_hotkey_can_only_be_registered_once()
    {
        // Ctrl+Alt+Shift+F24 is free on practically every PC.
        var hotkey = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, Keys.F24);
        using var first = new HotkeyListener();
        using var second = new HotkeyListener();

        Assert.True(first.Register(hotkey, SaveMode.Mixed));
        Assert.False(second.Register(hotkey, SaveMode.Mixed));
    }
}
