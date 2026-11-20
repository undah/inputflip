using System.Diagnostics.CodeAnalysis;

namespace Switchboard.Core.Hotkeys;

/// <summary>Same values as the MOD_* flags for RegisterHotKey.</summary>
[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Win = 0x8,
}

/// <summary>A global shortcut such as "Ctrl+Alt+Shift+I", parsed from settings.</summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, uint VirtualKey)
{
    private static readonly Dictionary<string, uint> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Tab"] = 0x09, ["Enter"] = 0x0D, ["Pause"] = 0x13, ["Space"] = 0x20,
        ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["End"] = 0x23, ["Home"] = 0x24,
        ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28,
        ["Insert"] = 0x2D, ["Delete"] = 0x2E,
    };

    public static bool TryParse(string? text, out Hotkey hotkey, [NotNullWhen(false)] out string? error)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "The shortcut is empty.";
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            error = "The shortcut is empty.";
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            HotkeyModifiers? m = parts[i].ToLowerInvariant() switch
            {
                "ctrl" or "control" => HotkeyModifiers.Control,
                "alt" => HotkeyModifiers.Alt,
                "shift" => HotkeyModifiers.Shift,
                "win" or "windows" => HotkeyModifiers.Win,
                _ => null,
            };
            if (m is null)
            {
                error = $"\"{parts[i]}\" isn't a modifier. Use Ctrl, Alt, Shift or Win.";
                return false;
            }
            modifiers |= m.Value;
        }

        if (!TryParseKey(parts[^1], out uint vk))
        {
            error = $"\"{parts[^1]}\" isn't a key Switchboard knows. Use a letter, a digit, F1 to F24, Numpad0 to Numpad9, or a key such as Home or PageUp.";
            return false;
        }

        bool isHighFunctionKey = vk is >= 0x7C and <= 0x87; // F13 to F24 are free on most keyboards
        if ((modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Win)) == 0 && !isHighFunctionKey)
        {
            error = "A shortcut needs Ctrl, Alt or Win, so it doesn't take over normal typing.";
            return false;
        }

        hotkey = new Hotkey(modifiers, vk);
        error = null;
        return true;
    }

    /// <summary>
    /// For the shortcut recorder: a shortcut from the keys pressed, if Switchboard can use it.
    /// Unknown keys and combinations without Ctrl, Alt or Win give an error to show.
    /// </summary>
    public static bool TryCreate(HotkeyModifiers modifiers, uint virtualKey, out Hotkey hotkey, [NotNullWhen(false)] out string? error)
    {
        hotkey = default;
        string key = KeyName(virtualKey);
        if (key.StartsWith("0x", StringComparison.Ordinal))
        {
            error = "Switchboard can't use that key. Use a letter, a digit, F1 to F24, a number pad digit, or a key such as Home or PageUp.";
            return false;
        }
        return TryParse(new Hotkey(modifiers, virtualKey).ToString(), out hotkey, out error);
    }

    /// <summary>Ctrl, Alt, Shift and Win on their own: the recorder waits for the next key.</summary>
    public static bool IsModifierKey(uint virtualKey) =>
        virtualKey is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5);

    /// <summary>Same shortcut, however it's written ("ctrl+shift+alt+i" equals "Ctrl+Alt+Shift+I").</summary>
    public static bool SameShortcut(string? a, string? b) =>
        TryParse(a, out var x, out _) && TryParse(b, out var y, out _) && x == y;

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(KeyName(VirtualKey));
        return string.Join("+", parts);
    }

    private static bool TryParseKey(string key, out uint vk)
    {
        vk = 0;
        if (key.Length == 1)
        {
            char c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                vk = c;
                return true;
            }
            return false;
        }
        if (NamedKeys.TryGetValue(key, out vk)) return true;
        if (key.Length is 2 or 3 && (key[0] is 'F' or 'f') && int.TryParse(key.AsSpan(1), out int f) && f is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + f - 1);
            return true;
        }
        if (key.StartsWith("Numpad", StringComparison.OrdinalIgnoreCase) && key.Length == 7 && char.IsDigit(key[6]))
        {
            vk = (uint)(0x60 + (key[6] - '0'));
            return true;
        }
        return false;
    }

    private static string KeyName(uint vk)
    {
        if (vk is >= 'A' and <= 'Z' or >= '0' and <= '9') return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return $"F{vk - 0x70 + 1}";
        if (vk is >= 0x60 and <= 0x69) return $"Numpad{vk - 0x60}";
        foreach (var (name, code) in NamedKeys)
            if (code == vk) return name;
        return $"0x{vk:X2}";
    }
}
