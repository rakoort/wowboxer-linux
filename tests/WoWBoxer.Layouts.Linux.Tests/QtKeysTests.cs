using WoWBoxer.Layouts.Core;

namespace WoWBoxer.Layouts.Linux.Tests;

public class QtKeysTests
{
    private const int CtrlAltShift = QtKeys.ControlModifier | QtKeys.AltModifier | QtKeys.ShiftModifier;
    private const int CtrlAlt = QtKeys.ControlModifier | QtKeys.AltModifier;

    [Fact]
    public void Every_key_core_can_parse_has_a_qt_code()
    {
        for (var vk = 1; vk < 256; vk++)
        {
            var name = Hotkeys.KeyName(vk);
            if (name.StartsWith("Key", StringComparison.Ordinal)) continue;   // Core cannot produce it
            Assert.True(Hotkeys.TryParse("Ctrl+Alt+" + name, out var parsed), name);
            Assert.NotEqual(0, QtKeys.KeyOf(parsed.Key));
        }
    }

    [Theory]
    [InlineData(Hotkeys.VkRight, 0x01000014)]
    [InlineData(Hotkeys.VkLeft, 0x01000012)]
    [InlineData(Hotkeys.VkBack, 0x01000003)]
    [InlineData(Hotkeys.VkEscape, 0x01000000)]
    [InlineData(Hotkeys.VkReturn, 0x01000004)]
    [InlineData(Hotkeys.VkSpace, 0x20)]
    [InlineData(Hotkeys.VkPageDown, 0x01000017)]
    [InlineData(Hotkeys.VkF1, 0x01000030)]
    [InlineData(Hotkeys.VkF1 + 23, 0x01000047)]
    [InlineData(Hotkeys.VkNumPad0 + 5, QtKeys.KeypadModifier | '5')]
    [InlineData('A', 'A')]
    [InlineData('7', '7')]
    [InlineData(0xBD, '-')]
    [InlineData(0xDE, '\'')]
    [InlineData(0xC0, '`')]
    public void Windows_virtual_keys_map_to_qt_keys(int vk, int qt)
    {
        Assert.Equal(qt, QtKeys.KeyOf(vk));
    }

    [Fact]
    public void Modifiers_map_and_win_is_meta()
    {
        Assert.Equal(QtKeys.ControlModifier | QtKeys.AltModifier | QtKeys.ShiftModifier | QtKeys.MetaModifier,
            QtKeys.ModifiersOf(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Shift | HotkeyModifiers.Win));
    }

    [Fact]
    public void Default_swap_keys_with_arrows_keep_shift()
    {
        Assert.Equal([CtrlAltShift | 0x01000014], QtKeys.CombinationsOf(Hotkeys.Defaults[SwapActions.Next], ["us"]));
        Assert.Equal([CtrlAltShift | 0x01000003], QtKeys.CombinationsOf(Hotkeys.Defaults[SwapActions.Home], ["us"]));
    }

    [Fact]
    public void Shifted_digits_also_register_the_symbol_the_layout_makes()
    {
        // Ctrl+Alt+Shift+1 arrives in KWin as Ctrl+Alt+! (Shift used up by the layout)
        Assert.Equal([CtrlAltShift | '1', CtrlAlt | '!'], QtKeys.CombinationsOf(Hotkeys.Defaults[SwapActions.Client(1)], ["us"]));
        Assert.Equal([CtrlAltShift | '2', CtrlAlt | '@'], QtKeys.CombinationsOf(Hotkeys.Defaults[SwapActions.Client(2)], ["us"]));
        // German layout: Shift+3 = §, Shift+0 = =; both layouts listed: both symbols
        Assert.Equal([CtrlAltShift | '3', CtrlAlt | 0xA7], QtKeys.CombinationsOf(Hotkeys.Defaults[SwapActions.Client(3)], ["de"]));
        Assert.Equal([CtrlAltShift | '0', CtrlAlt | '=', CtrlAlt | ')'], QtKeys.CombinationsOf(Hotkeys.Defaults[SwapActions.Client(10)], ["de", "us"]));
    }

    [Fact]
    public void Letters_and_unshifted_keys_have_one_form()
    {
        Assert.True(Hotkeys.TryParse("Ctrl+Shift+W", out var letter));
        Assert.Equal([QtKeys.ControlModifier | QtKeys.ShiftModifier | 'W'], QtKeys.CombinationsOf(letter, ["us"]));
        Assert.True(Hotkeys.TryParse("Ctrl+Alt+1", out var digit));
        Assert.Equal([CtrlAlt | '1'], QtKeys.CombinationsOf(digit, ["us"]));
        Assert.True(Hotkeys.TryParse("Alt+Shift+Num1", out var keypad));
        Assert.Equal([QtKeys.AltModifier | QtKeys.ShiftModifier | QtKeys.KeypadModifier | '1'], QtKeys.CombinationsOf(keypad, ["us"]));
    }

    [Fact]
    public void Swap_shortcut_names_match_the_agent()
    {
        Assert.Equal("wowboxer-swap-next", GlobalHotkeys.ShortcutName(SwapActions.Next));
        Assert.Equal("wowboxer-swap-client-10", GlobalHotkeys.ShortcutName(SwapActions.Client(10)));
        Assert.Equal("wowboxer-swap-home", GlobalHotkeys.ShortcutName(SwapActions.Home));
    }
}
