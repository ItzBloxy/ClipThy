namespace ClipThy.Hotkeys;

/// <summary>Modifier flags with the values RegisterHotKey expects (MOD_ALT, MOD_CONTROL, MOD_SHIFT, MOD_WIN).</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Win = 0x8,
}

/// <summary>A global hotkey such as "Alt+F10". <see cref="Key"/> is a Windows virtual-key code.</summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, Keys Key)
{
    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string[] parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Any(p => p.Length == 0)) return false;

        var modifiers = HotkeyModifiers.None;
        foreach (string part in parts[..^1])
        {
            HotkeyModifiers modifier = part.ToLowerInvariant() switch
            {
                "alt" => HotkeyModifiers.Alt,
                "ctrl" or "control" => HotkeyModifiers.Control,
                "shift" => HotkeyModifiers.Shift,
                "win" => HotkeyModifiers.Win,
                _ => HotkeyModifiers.None,
            };
            if (modifier == HotkeyModifiers.None || modifiers.HasFlag(modifier)) return false;
            modifiers |= modifier;
        }

        if (!TryParseKey(parts[^1], out Keys key)) return false;
        hotkey = new Hotkey(modifiers, key);
        return true;
    }

    private static bool TryParseKey(string token, out Keys key)
    {
        key = Keys.None;
        if (token.Length == 1 && char.IsAsciiDigit(token[0]))
        {
            key = Keys.D0 + (token[0] - '0');
            return true;
        }

        // Reject numbers ("10" would parse as a raw enum value) and flag lists ("A, B").
        if (token.All(char.IsAsciiDigit) || token.Contains(',')) return false;
        if (!Enum.TryParse(token, ignoreCase: true, out key)) return false;

        bool isModifierKey = key is Keys.ShiftKey or Keys.ControlKey or Keys.Menu or Keys.LWin or Keys.RWin;
        bool isPlainKeyCode = key != Keys.None && ((int)key & ~0xFF) == 0;
        return isPlainKeyCode && !isModifierKey;
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(Key.ToString());
        return string.Join('+', parts);
    }
}
