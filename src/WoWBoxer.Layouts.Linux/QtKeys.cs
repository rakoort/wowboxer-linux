using WoWBoxer.Layouts.Core;

namespace WoWBoxer.Layouts.Linux;

/// <summary>
/// Core's hotkeys (Windows virtual-key codes, RegisterHotKey modifiers) as Qt key codes for kglobalaccel. KWin matches a
/// key press as Qt sees it after the keyboard layout: Shift on a digit or punctuation key is used up by the layout
/// (Ctrl+Alt+Shift+1 arrives as Ctrl+Alt+! on a US keyboard). So such a hotkey is registered with its shifted symbol on
/// the user's layouts too, next to the plain form.
/// </summary>
public static class QtKeys
{
    public const int ShiftModifier = 0x02000000, ControlModifier = 0x04000000, AltModifier = 0x08000000,
        MetaModifier = 0x10000000, KeypadModifier = 0x20000000;

    /// <summary>The Qt key (no modifiers) for a Windows virtual-key code, or 0 if Qt has none.</summary>
    public static int KeyOf(int vk) => vk switch
    {
        >= '0' and <= '9' or >= 'A' and <= 'Z' => vk,   // Qt::Key_0 … Key_9, Key_A … Key_Z share the ASCII codes
        >= Hotkeys.VkF1 and <= Hotkeys.VkF1 + 23 => 0x01000030 + (vk - Hotkeys.VkF1),   // Key_F1 … Key_F24
        >= Hotkeys.VkNumPad0 and <= Hotkeys.VkNumPad0 + 9 => KeypadModifier | ('0' + vk - Hotkeys.VkNumPad0),
        Hotkeys.VkEscape => 0x01000000,
        Hotkeys.VkTab => 0x01000001,
        Hotkeys.VkBack => 0x01000003,
        Hotkeys.VkReturn => 0x01000004,
        Hotkeys.VkInsert => 0x01000006,
        Hotkeys.VkDelete => 0x01000007,
        Hotkeys.VkSnapshot => 0x01000009,
        Hotkeys.VkHome => 0x01000010,
        Hotkeys.VkEnd => 0x01000011,
        Hotkeys.VkLeft => 0x01000012,
        Hotkeys.VkUp => 0x01000013,
        Hotkeys.VkRight => 0x01000014,
        Hotkeys.VkDown => 0x01000015,
        Hotkeys.VkPageUp => 0x01000016,
        Hotkeys.VkPageDown => 0x01000017,
        Hotkeys.VkSpace => 0x20,
        0xBA => ';', 0xBB => '=', 0xBC => ',', 0xBD => '-', 0xBE => '.', 0xBF => '/', 0xC0 => '`',
        0xDB => '[', 0xDC => '\\', 0xDD => ']', 0xDE => '\'',
        _ => 0,
    };

    /// <summary>The Qt modifier bits of Core's modifiers (Win = Meta).</summary>
    public static int ModifiersOf(HotkeyModifiers m) =>
        (m.HasFlag(HotkeyModifiers.Shift) ? ShiftModifier : 0) | (m.HasFlag(HotkeyModifiers.Ctrl) ? ControlModifier : 0)
        | (m.HasFlag(HotkeyModifiers.Alt) ? AltModifier : 0) | (m.HasFlag(HotkeyModifiers.Win) ? MetaModifier : 0);

    /// <summary>
    /// The key codes (modifiers | key) to register for a hotkey: the plain combination first, then – for Shift with a
    /// digit or punctuation key – the shifted symbol without Shift for each layout (us, de, gb, fr …; unknown layouts
    /// count as us). Empty if the key has no Qt code.
    /// </summary>
    public static IReadOnlyList<int> CombinationsOf(Hotkey hotkey, IReadOnlyList<string>? layouts = null)
    {
        var key = KeyOf(hotkey.Key);
        if (key == 0) return [];
        var mods = ModifiersOf(hotkey.Modifiers);
        var result = new List<int> { mods | key };
        if ((mods & ShiftModifier) == 0 || (key & 0x01000000) != 0 || (key & KeypadModifier) != 0 || key is >= 'A' and <= 'Z' or 0x20)
            return result;
        foreach (var layout in layouts is { Count: > 0 } ? layouts : ["us"])
            if (ShiftedSymbol(layout, (char)key) is { } symbol && !result.Contains((mods & ~ShiftModifier) | symbol))
                result.Add((mods & ~ShiftModifier) | symbol);
        return result;
    }

    /// <summary>The Qt key of the symbol Shift gives on a digit or punctuation key in a layout (null if unknown).</summary>
    internal static int? ShiftedSymbol(string layout, char key)
    {
        var (keys, shifted) = layout switch
        {
            "de" or "at" or "ch" => ("1234567890", "!\"§$%&/()="),
            "gb" => ("1234567890-=[];'#,./`", "!\"£$%^&*()_+{}:@~<>?¬"),
            "fr" => ("", ""),   // AZERTY digits are shifted already: no extra form helps
            _ => ("1234567890-=[];',./`\\", "!@#$%^&*()_+{}:\"<>?~|"),
        };
        var i = keys.IndexOf(key);
        return i < 0 ? null : QtKeyOfSymbol(shifted[i]);
    }

    /// <summary>Qt's key code for a printable symbol: its upper-case Latin-1 code point (Qt::Key_Exclam = '!', Key_section = '§').</summary>
    private static int QtKeyOfSymbol(char c) => char.ToUpperInvariant(c);

    /// <summary>The keyboard layouts the user set in Plasma (<c>~/.config/kxkbrc</c> LayoutList), else ["us"].</summary>
    public static IReadOnlyList<string> UserLayouts()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "kxkbrc");
        try
        {
            if (!File.Exists(path)) return ["us"];
            foreach (var line in File.ReadLines(path))
                if (line.StartsWith("LayoutList=", StringComparison.Ordinal))
                {
                    var list = line["LayoutList=".Length..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    if (list.Length > 0) return list;
                }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return ["us"];
    }
}
