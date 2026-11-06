using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using WoWBoxer.Layouts.App.ViewModels;
using WoWBoxer.Layouts.Core;
using WoWBoxer.Layouts.Linux;

namespace WoWBoxer.Layouts.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    // ---------- window swap: hotkey boxes ("press the keys") ----------

    private void OnHotkeyBoxFocus(object? sender, FocusChangedEventArgs e)
    {
        if (sender is not TextBox box) return;
        (DataContext as MainViewModel)?.PauseHotkeysForEditing();   // otherwise KWin would take a set combination before the box sees it
        box.SetCurrentValue(TextBox.TextProperty, "Press the keys…");   // keeps the binding
    }

    private void OnHotkeyBoxBlur(object? sender, FocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: HotkeyRow row } box) box.SetCurrentValue(TextBox.TextProperty, row.Text);
        (DataContext as MainViewModel)?.ResumeHotkeys();
    }

    /// <summary>Reads the combination from the app's own key event (only while this box has focus; no hook).</summary>
    private void OnHotkeyBoxKey(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not HotkeyRow row) return;
        e.Handled = true;
        var m = e.KeyModifiers;
        if (e.Key == Key.Escape && m == KeyModifiers.None)
        {
            ClearFocus();
            return;
        }
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            box.SetCurrentValue(TextBox.TextProperty, ModifierText(m) + "…");
            return;
        }
        var mods = HotkeyModifiers.None;
        if (m.HasFlag(KeyModifiers.Control)) mods |= HotkeyModifiers.Ctrl;
        if (m.HasFlag(KeyModifiers.Alt)) mods |= HotkeyModifiers.Alt;
        if (m.HasFlag(KeyModifiers.Shift)) mods |= HotkeyModifiers.Shift;
        if (m.HasFlag(KeyModifiers.Meta)) mods |= HotkeyModifiers.Win;
        var vk = VirtualKey(e.Key);
        if (vk == 0) return;
        ClearFocus();   // sets the hotkeys again (blur), then the new combination is saved
        row.Capture(new Hotkey(mods, vk).ToString());
    }

    private void ClearFocus() => TopLevel.GetTopLevel(this)?.FocusManager?.Focus(null);

    private static string ModifierText(KeyModifiers m) =>
        string.Concat(m.HasFlag(KeyModifiers.Control) ? "Ctrl+" : "", m.HasFlag(KeyModifiers.Alt) ? "Alt+" : "", m.HasFlag(KeyModifiers.Shift) ? "Shift+" : "", m.HasFlag(KeyModifiers.Meta) ? "Win+" : "");

    /// <summary>The Windows virtual-key code Core's hotkeys store (the backend turns it into a Qt key for KWin); 0 = not supported.</summary>
    private static int VirtualKey(Key key) => key switch
    {
        >= Key.A and <= Key.Z => 0x41 + (key - Key.A),
        >= Key.D0 and <= Key.D9 => 0x30 + (key - Key.D0),
        >= Key.NumPad0 and <= Key.NumPad9 => 0x60 + (key - Key.NumPad0),
        >= Key.F1 and <= Key.F24 => 0x70 + (key - Key.F1),
        Key.Back => 0x08, Key.Tab => 0x09, Key.Enter => 0x0D, Key.Pause => 0x13, Key.Space => 0x20,
        Key.PageUp => 0x21, Key.PageDown => 0x22, Key.End => 0x23, Key.Home => 0x24,
        Key.Left => 0x25, Key.Up => 0x26, Key.Right => 0x27, Key.Down => 0x28,
        Key.Insert => 0x2D, Key.Delete => 0x2E,
        Key.Multiply => 0x6A, Key.Add => 0x6B, Key.Subtract => 0x6D, Key.Decimal => 0x6E, Key.Divide => 0x6F,
        Key.OemSemicolon => 0xBA, Key.OemPlus => 0xBB, Key.OemComma => 0xBC, Key.OemMinus => 0xBD, Key.OemPeriod => 0xBE,
        Key.OemQuestion => 0xBF, Key.OemTilde => 0xC0, Key.OemOpenBrackets => 0xDB, Key.OemPipe => 0xDC, Key.OemCloseBrackets => 0xDD,
        Key.OemQuotes => 0xDE, Key.OemBackslash => 0xE2,
        _ => 0,
    };

    // ---------- window swap: custom order chips (drag to move, click to remove) ----------

    private Point _chipDown;
    private OrderChip? _chipPressed;
    private bool _chipDragging;

    private void OnChipDown(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control chip || !e.GetCurrentPoint(chip).Properties.IsLeftButtonPressed) return;
        _chipDown = e.GetPosition(this);
        _chipPressed = chip.DataContext as OrderChip;
        _chipDragging = false;
        e.Pointer.Capture(chip);
        e.Handled = true;
    }

    private void OnChipMove(object? sender, PointerEventArgs e)
    {
        if (_chipPressed is null) return;
        var d = e.GetPosition(this) - _chipDown;
        if (Math.Abs(d.X) >= 6 || Math.Abs(d.Y) >= 6) _chipDragging = true;
    }

    private void OnChipUp(object? sender, PointerReleasedEventArgs e)
    {
        var chip = _chipPressed;
        var dragging = _chipDragging;
        _chipPressed = null;
        _chipDragging = false;
        e.Pointer.Capture(null);
        if (chip is null || DataContext is not MainViewModel vm) return;
        if (!dragging)
        {
            vm.RemoveFromSwapOrderCommand.Execute(chip);   // a click, not a drag
            return;
        }
        if (ChipList.InputHitTest(e.GetPosition(ChipList)) is Visual hit
            && hit.FindAncestorOfType<ContentPresenter>(includeSelf: true) is { DataContext: OrderChip to } && to != chip)
            vm.MoveSwapOrderChip(chip.Position, to.Position);
    }

    private void OnChipCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _chipPressed = null;
        _chipDragging = false;
    }

    /// <summary>
    /// A desktop shortcut (a .desktop file on the desktop) that opens the app in the tray and starts the current layout's
    /// squad.
    /// </summary>
    private void OnCreateShortcut(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel { SelectedProfile: { } profile } vm || Environment.ProcessPath is not { } exe) return;
        var name = $"{profile.Name} - WoWBoxer";
        try
        {
            var desktop = DesktopFolder();
            Directory.CreateDirectory(desktop);
            var path = Path.Combine(desktop, Path.ChangeExtension(ProfileStore.FileNameFor(name), ".desktop"));
            File.WriteAllText(path, string.Join('\n',
                "[Desktop Entry]",
                "Type=Application",
                $"Name={name}",
                $"Comment=Open the WoW clients and place their windows ({profile.Name})",
                $"Exec={DesktopQuote(exe)} --tray --start {DesktopQuote(profile.Name)}",
                "Icon=wowboxer",
                "Terminal=false",
                ""));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute);   // Plasma runs only executable desktop files
            vm.Status.Report(StatusLevel.Ok, $"Desktop shortcut created: {Path.GetFileName(path)}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            vm.Status.Report(StatusLevel.Warn, $"Could not create the shortcut: {ex.Message}");
        }
    }

    /// <summary>The desktop folder (XDG_DESKTOP_DIR from user-dirs.dirs, else ~/Desktop).</summary>
    private static string DesktopFolder()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c ? c : Path.Combine(home, ".config");
        var dirs = Path.Combine(config, "user-dirs.dirs");
        if (File.Exists(dirs))
            foreach (var line in File.ReadLines(dirs))
                if (line.StartsWith("XDG_DESKTOP_DIR=", StringComparison.Ordinal))
                    return line["XDG_DESKTOP_DIR=".Length..].Trim('"').Replace("$HOME", home, StringComparison.Ordinal);
        return Path.Combine(home, "Desktop");
    }

    /// <summary>An Exec argument in the Desktop Entry quoting rules.</summary>
    private static string DesktopQuote(string arg) =>
        "\"" + arg.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$").Replace("%", "%%") + "\"";
}
